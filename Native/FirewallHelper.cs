using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace VibeDesk.Native
{
    public static class FirewallHelper
    {
        public static bool IsAdministrator()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        public static bool AddFirewallRules(bool elevateIfNeed = false)
        {
            try
            {
                string exePath = Environment.ProcessPath ?? "";

                string batchContent = "@echo off\r\n" +
                    "netsh advfirewall firewall delete rule name=\"VibeDesk UDP 15890\" >nul 2>&1\r\n" +
                    "netsh advfirewall firewall delete rule name=\"VibeDesk TCP 15890\" >nul 2>&1\r\n" +
                    "netsh advfirewall firewall delete rule name=\"VibeDesk Application\" >nul 2>&1\r\n" +
                    "netsh advfirewall firewall add rule name=\"VibeDesk UDP 15890\" dir=in action=allow protocol=UDP localport=15890\r\n" +
                    "netsh advfirewall firewall add rule name=\"VibeDesk TCP 15890\" dir=in action=allow protocol=TCP localport=15890\r\n";

                if (!string.IsNullOrEmpty(exePath))
                {
                    batchContent += $"netsh advfirewall firewall add rule name=\"VibeDesk Application\" dir=in action=allow program=\"{exePath}\" enable=yes\r\n";
                }

                string tempBatch = Path.Combine(Path.GetTempPath(), "vibedesk_fw.bat");
                File.WriteAllText(tempBatch, batchContent);

                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{tempBatch}\"",
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                if (IsAdministrator())
                {
                    var proc = Process.Start(psi);
                    proc?.WaitForExit(5000);
                    try { File.Delete(tempBatch); } catch { }
                    return proc?.ExitCode == 0;
                }
                else if (elevateIfNeed)
                {
                    psi.Verb = "runas";
                    var proc = Process.Start(psi);
                    proc?.WaitForExit(10000);
                    try { File.Delete(tempBatch); } catch { }
                    return proc?.ExitCode == 0;
                }
            }
            catch { }

            return false;
        }
    }
}
