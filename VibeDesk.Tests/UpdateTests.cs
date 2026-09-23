using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VibeDesk.Update;

namespace VibeDesk.Tests
{
    [TestClass]
    public class UpdateTests
    {
        [TestMethod]
        public void TestVersionComparisonLogic()
        {
            // Newer versions should return true
            Assert.IsTrue(AppVersion.IsNewer("v1.2.0", "1.1.0"));
            Assert.IsTrue(AppVersion.IsNewer("1.1.1", "1.1.0"));
            Assert.IsTrue(AppVersion.IsNewer("2.0.0", "1.9.9"));
            Assert.IsTrue(AppVersion.IsNewer("v1.1.0.1", "1.1.0"));
            Assert.IsTrue(AppVersion.IsNewer("v1.2.0-beta.1", "1.1.0"));

            // Same or older versions should return false
            Assert.IsFalse(AppVersion.IsNewer("v1.1.0", "1.1.0"));
            Assert.IsFalse(AppVersion.IsNewer("1.1.0", "1.1.0"));
            Assert.IsFalse(AppVersion.IsNewer("1.0.9", "1.1.0"));
            Assert.IsFalse(AppVersion.IsNewer("v0.9.0", "1.1.0"));
            Assert.IsFalse(AppVersion.IsNewer(null, "1.1.0"));
            Assert.IsFalse(AppVersion.IsNewer("", "1.1.0"));
            Assert.IsFalse(AppVersion.IsNewer("invalid-tag", "1.1.0"));
        }

        [TestMethod]
        public void TestNormalizeVersion()
        {
            var v1 = AppVersion.NormalizeVersion("v1.2.3");
            Assert.IsNotNull(v1);
            Assert.AreEqual(new Version(1, 2, 3), v1);

            var v2 = AppVersion.NormalizeVersion("2.5");
            Assert.IsNotNull(v2);
            Assert.AreEqual(new Version(2, 5, 0), v2);

            var v3 = AppVersion.NormalizeVersion("v3");
            Assert.IsNotNull(v3);
            Assert.AreEqual(new Version(3, 0, 0), v3);

            var v4 = AppVersion.NormalizeVersion("v1.3.0-rc1+sha123");
            Assert.IsNotNull(v4);
            Assert.AreEqual(new Version(1, 3, 0), v4);
        }

        [TestMethod]
        public void TestGitHubReleaseJsonParsing()
        {
            string mockJson = @"
            {
                ""tag_name"": ""v1.2.0"",
                ""name"": ""VibeDesk v1.2.0 Release"",
                ""body"": ""- Added 60-120 FPS\n- Added resolution presets"",
                ""assets"": [
                    {
                        ""name"": ""source.zip"",
                        ""browser_download_url"": ""https://github.com/Gera9997/VibeDesk/releases/download/v1.2.0/source.zip"",
                        ""size"": 1024
                    },
                    {
                        ""name"": ""VibeDesk.exe"",
                        ""browser_download_url"": ""https://github.com/Gera9997/VibeDesk/releases/download/v1.2.0/VibeDesk.exe"",
                        ""size"": 65000000
                    }
                ]
            }";

            using var doc = JsonDocument.Parse(mockJson);
            var root = doc.RootElement;

            string tagName = root.GetProperty("tag_name").GetString()!;
            string body = root.GetProperty("body").GetString()!;

            string downloadUrl = "";
            long sizeBytes = 0;

            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString()!;
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = asset.GetProperty("browser_download_url").GetString()!;
                    sizeBytes = asset.GetProperty("size").GetInt64();
                    break;
                }
            }

            Assert.AreEqual("v1.2.0", tagName);
            Assert.IsTrue(AppVersion.IsNewer(tagName, "1.1.0"));
            Assert.AreEqual("https://github.com/Gera9997/VibeDesk/releases/download/v1.2.0/VibeDesk.exe", downloadUrl);
            Assert.AreEqual(65000000L, sizeBytes);
            Assert.IsTrue(body.Contains("120 FPS"));
        }

        [TestMethod]
        public void TestFileMoveRunningExecutable()
        {
            // Test moving the current running process or a spawned test process
            string testDir = Path.Combine(Path.GetTempPath(), "VibeDesk_TestUpdate_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testDir);

            try
            {
                string originalExe = Path.Combine(testDir, "TestApp.exe");
                string backupExe = Path.Combine(testDir, "TestApp.exe.bak");
                string newExe = Path.Combine(testDir, "TestApp_update.exe");

                // Copy real VibeDesk.exe or cmd.exe to originalExe and launch it!
                File.Copy(Environment.ProcessPath ?? "cmd.exe", originalExe, overwrite: true);
                File.WriteAllBytes(backupExe, new byte[512]);
                File.WriteAllBytes(newExe, new byte[2048]);

                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = originalExe,
                    Arguments = "--test-silent",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });

                try
                {
                    Thread.Sleep(500);
                    // DO NOT delete backupExe first - test if File.Move(..., overwrite: true) works directly!
                    File.Move(originalExe, backupExe, overwrite: true);
                    File.Move(newExe, originalExe, overwrite: true);

                    Assert.IsTrue(File.Exists(originalExe));
                    Assert.AreEqual(2048, new FileInfo(originalExe).Length);
                    Assert.IsTrue(File.Exists(backupExe));
                }
                finally
                {
                    try { proc?.Kill(); proc?.WaitForExit(1000); } catch { }
                }
            }
            finally
            {
                try { Directory.Delete(testDir, true); } catch { }
            }
        }
    }
}
