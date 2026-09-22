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
                
                // Rule 1: Allow UDP port 15890 for all profiles (Private and Public)
                string argsPort = "advfirewall firewall add rule name=\"VibeDesk UDP 15890\" dir=in action=allow protocol=UDP localport=15890 profile=any";
                
                // Rule 2: Allow application binary for all profiles
                string argsApp = string.IsNullOrEmpty(exePath) 
                    ? "" 
                    : $"advfirewall firewall add rule name=\"VibeDesk Application\" dir=in action=allow program=\"{exePath}\" enable=yes profile=any";

                if (IsAdministrator())
                {
                    RunNetsh(argsPort, false);
                    if (!string.IsNullOrEmpty(argsApp))
                    {
                        RunNetsh(argsApp, false);
                    }
                    return true;
                }
                else if (elevateIfNeed)
                {
                    // Run elevated batch via cmd to apply both rules at once
                    string tempBatch = Path.Combine(Path.GetTempPath(), "vibedesk_fw.bat");
                    string batchContent = $"@echo off\r\nnetsh {argsPort}\r\n";
                    if (!string.IsNullOrEmpty(argsApp))
                    {
                        batchContent += $"netsh {argsApp}\r\n";
                    }
                    File.WriteAllText(tempBatch, batchContent);

                    var psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c \"{tempBatch}\"",
                        Verb = "runas",
                        UseShellExecute = true,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };

                    var proc = Process.Start(psi);
                    proc?.WaitForExit(10000);
                    try { File.Delete(tempBatch); } catch { }
                    return proc?.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static void RunNetsh(string arguments, bool elevated)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = arguments,
                UseShellExecute = elevated,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            if (elevated) psi.Verb = "runas";

            var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
        }
    }
}
