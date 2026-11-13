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

        public static async Task<IPEndPoint?> QueryAsync(UdpClient udp, string stunHost, int stunPort, int timeoutMs = 2000)
        {
            using var cts = new System.Threading.CancellationTokenSource(timeoutMs);
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(stunHost);
                var ipv4 = System.Linq.Enumerable.FirstOrDefault(addrs, a => a.AddressFamily == AddressFamily.InterNetwork);
                if (ipv4 == null) return null;

                byte[] request = CreateBindingRequest();
                await udp.SendAsync(request, request.Length, new IPEndPoint(ipv4, stunPort));
                var result = await udp.ReceiveAsync(cts.Token);
                return ParseResponse(result.Buffer);
            }
            catch
            {
                return null;
            }
        }

        public static async Task<(IPEndPoint? EndPoint, bool IsSymmetric, string Message)> ProbeAsync(int localPort, string localIp = "")
        {
            IPAddress bindAddr = IPAddress.Any;
            if (!string.IsNullOrEmpty(localIp) && IPAddress.TryParse(localIp, out var parsedIp))
            {
                bindAddr = parsedIp;
            }

            try
            {
                using var probe = new UdpClient();
                probe.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                if (localPort > 0 || !bindAddr.Equals(IPAddress.Any))
                {
                    try
                    {
                        probe.Client.Bind(new IPEndPoint(bindAddr, localPort));
                    }
                    catch
                    {
                        // Fallback to binding to any interface on this port
                        if (localPort > 0) probe.Client.Bind(new IPEndPoint(IPAddress.Any, localPort));
                    }
                }

                // 1. Query Cloudflare STUN first (highly reliable on standard port 3478, rarely blocked)
                var cfEp = await QueryAsync(probe, "stun.cloudflare.com", 3478, timeoutMs: 2000);

                // 2. Query Google STUN
                var googleEp = await QueryAsync(probe, "stun.l.google.com", 19302, timeoutMs: 2000);

                // 3. If both failed, try fallback stun1.l.google.com
                if (cfEp == null && googleEp == null)
                {
                    googleEp = await QueryAsync(probe, "stun1.l.google.com", 19302, timeoutMs: 2000);
                }

                bool isSymmetric = false;
                if (googleEp != null && cfEp != null)
                {
                    isSymmetric = googleEp.Port != cfEp.Port;
                }

                var primary = cfEp ?? googleEp;
                if (primary != null)
                {
                    string natType = isSymmetric ? "Symmetric NAT (порт меняется)" : "Cone/EIM NAT (порт постоянный)";
                    return (primary, isSymmetric, $"STUN успешен: {primary.Address}:{primary.Port} [{natType}]");
                }
            }
            catch (Exception ex)
            {
                return (null, false, $"Ошибка STUN probe: {ex.Message}");
            }

            return (null, false, "STUN серверы не ответили (возможно, UDP блокируется провайдером)");
        }

        public static async Task<IPAddress?> GetPublicIpHttpAsync(int timeoutMs = 2500)
        {
            string[] services = new[]
            {
                "https://api.ipify.org",
                "https://icanhazip.com",
                "https://checkip.amazonaws.com"
            };

            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            foreach (var url in services)
            {
                try
                {
                    string res = (await http.GetStringAsync(url)).Trim();
                    if (IPAddress.TryParse(res, out var parsed))
                    {
                        return parsed;
                    }
                }
                catch { }
            }
            return null;
        }

        public static async Task<(IPEndPoint? EndPoint, string Message)> ResolveAsync(int localPort = 0, string localIp = "")
        {
            var (ep, _, msg) = await ProbeAsync(localPort, localIp);
            return (ep, msg);
        }

        public static async Task SendStunQueriesViaNetManagerAsync(LiteNetLib.NetManager netManager)
        {
            if (netManager == null || !netManager.IsRunning) return;
            byte[] req = CreateBindingRequest();

            try
            {
                var cf = await Dns.GetHostAddressesAsync("stun.cloudflare.com");
                var cfIp = System.Linq.Enumerable.FirstOrDefault(cf, a => a.AddressFamily == AddressFamily.InterNetwork);
                if (cfIp != null)
                    netManager.SendUnconnectedMessage(req, new IPEndPoint(cfIp, 3478));
            }
            catch { }

            try
            {
                var gg = await Dns.GetHostAddressesAsync("stun.l.google.com");
                var ggIp = System.Linq.Enumerable.FirstOrDefault(gg, a => a.AddressFamily == AddressFamily.InterNetwork);
                if (ggIp != null)
                    netManager.SendUnconnectedMessage(req, new IPEndPoint(ggIp, 19302));
            }
            catch { }
        }

        public static byte[] CreateBindingRequest()
        {
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
            return request;
        }

        public static IPEndPoint? ParseResponse(byte[] response)
        {
            if (response == null || response.Length < 20 || response[0] != 0x01 || response[1] != 0x01)
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
    }
}
