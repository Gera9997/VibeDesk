using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace VibeDesk.Update
{
    public class UpdateInfo
    {
        public bool HasUpdate { get; set; }
        public string LatestVersion { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public long FileSizeBytes { get; set; }
        public string ReleaseNotes { get; set; } = "";
        public string ErrorMessage { get; set; } = "";
    }

    public class UpdateManager
    {
        public const string DefaultOwner = "Gera9997";
        public const string DefaultRepo = "VibeDesk";

        private readonly string _owner;
        private readonly string _repo;
        private static readonly HttpClient _apiClient = new()
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        private static readonly HttpClient _downloadClient = new()
        {
            Timeout = TimeSpan.FromMinutes(15) // Generous 15 minutes for 160MB download
        };

        static UpdateManager()
        {
            _apiClient.DefaultRequestHeaders.Add("User-Agent", "VibeDesk-AutoUpdater");
            _downloadClient.DefaultRequestHeaders.Add("User-Agent", "VibeDesk-AutoUpdater");
        }

        public UpdateManager(string owner = DefaultOwner, string repo = DefaultRepo)
        {
            _owner = owner;
            _repo = repo;
        }

        public static void CleanupLeftoverBackup()
        {
            try
            {
                string? currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? Environment.ProcessPath;
                if (!string.IsNullOrEmpty(currentExe))
                {
                    string backup = currentExe + ".bak";
                    if (File.Exists(backup))
                    {
                        File.Delete(backup);
                    }
                }
            }
            catch { }
        }

        public async Task<UpdateInfo> CheckForUpdateAsync(CancellationToken cancellationToken = default)
        {
            string url = $"https://api.github.com/repos/{_owner}/{_repo}/releases/latest";
            Exception? lastEx = null;

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    using var response = await _apiClient.GetAsync(url, cancellationToken);

                    if (!response.IsSuccessStatusCode)
                    {
                        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                        {
                            return new UpdateInfo
                            {
                                HasUpdate = false,
                                ErrorMessage = "Релизы на GitHub пока не найдены."
                            };
                        }

                        return new UpdateInfo
                        {
                            HasUpdate = false,
                            ErrorMessage = $"GitHub API ответил с ошибкой: {(int)response.StatusCode} {response.ReasonPhrase}"
                        };
                    }

                    string json = await response.Content.ReadAsStringAsync(cancellationToken);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    string tagName = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? "" : "";
                    string body = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "";

                    string downloadUrl = "";
                    long sizeBytes = 0;

                    if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var asset in assets.EnumerateArray())
                        {
                            string name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            {
                                downloadUrl = asset.TryGetProperty("browser_download_url", out var urlProp) ? urlProp.GetString() ?? "" : "";
                                sizeBytes = asset.TryGetProperty("size", out var sizeProp) ? sizeProp.GetInt64() : 0;
                                break;
                            }
                        }
                    }

                    bool hasUpdate = AppVersion.IsNewer(tagName);

                    return new UpdateInfo
                    {
                        HasUpdate = hasUpdate,
                        LatestVersion = tagName,
                        DownloadUrl = downloadUrl,
                        FileSizeBytes = sizeBytes,
                        ReleaseNotes = body
                    };
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    if (attempt < 3)
                    {
                        await Task.Delay(1000, cancellationToken);
                    }
                }
            }

            return new UpdateInfo
            {
                HasUpdate = false,
                ErrorMessage = $"Ошибка проверки обновлений: {lastEx?.Message}"
            };
        }

        public async Task<bool> DownloadAndApplyUpdateAsync(
            string downloadUrl,
            IProgress<(long received, long total, int percent)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(downloadUrl))
                throw new ArgumentException("Ссылка на скачивание пуста.", nameof(downloadUrl));

            string? currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentExe))
                throw new InvalidOperationException("Не удалось определить путь к запущенному исполняемому файлу.");

            string currentDir = Path.GetDirectoryName(currentExe)!;
            string updateFilePath = Path.Combine(currentDir, "VibeDesk_update.exe");

            // Clean up any stale partial downloads or updater scripts
            try { if (File.Exists(updateFilePath)) File.Delete(updateFilePath); } catch { }
            try { string staleUpdater = Path.Combine(currentDir, "VibeDesk_updater.cmd"); if (File.Exists(staleUpdater)) File.Delete(staleUpdater); } catch { }

            try
            {
                using var response = await _downloadClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                long totalBytes = response.Content.Headers.ContentLength ?? -1L;

                using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
                using (var fileStream = new FileStream(updateFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
                {
                    byte[] buffer = new byte[128 * 1024];
                    long totalRead = 0;
                    int read;

                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        totalRead += read;

                        if (totalBytes > 0)
                        {
                            int percent = (int)((totalRead * 100) / totalBytes);
                            progress?.Report((totalRead, totalBytes, percent));
                        }
                        else
                        {
                            progress?.Report((totalRead, -1, 0));
                        }
                    }
                }

                // Verify file integrity
                var fileInfo = new FileInfo(updateFilePath);
                if (!fileInfo.Exists || fileInfo.Length < 10 * 1024 * 1024)
                {
                    throw new IOException($"Скачанный файл обновления поврежден или имеет неполный размер ({fileInfo.Length} байт).");
                }

                // File downloaded completely. Now apply update and restart!
                ApplyUpdateAndRestart(updateFilePath, currentExe);
                return true;
            }
            catch (Exception)
            {
                try
                {
                    if (File.Exists(updateFilePath))
                    {
                        File.Delete(updateFilePath);
                    }
                }
                catch { }
                throw;
            }
        }

        private static void ApplyUpdateAndRestart(string newFilePath, string currentExe)
        {
            string backupPath = currentExe + ".bak";
            string currentDir = Path.GetDirectoryName(currentExe)!;
            string updaterBat = Path.Combine(currentDir, "VibeDesk_updater.cmd");
            int pid = Environment.ProcessId;

            // Generate self-contained, robust batch script:
            // 1. Waits for this PID to terminate
            // 2. Retries moving newFilePath -> currentExe with 10 attempts
            // 3. Starts currentExe
            // 4. Deletes updater script itself
            string script = $@"@echo off
setlocal
set PID={pid}
set TARGET=""{currentExe}""
set UPDATE=""{newFilePath}""
set BACKUP=""{backupPath}""

:WAIT_PROCESS
tasklist /fi ""PID eq %PID%"" 2>nul | find ""%PID%"" >nul
if not errorlevel 1 (
    timeout /t 1 /nobreak >nul
    goto WAIT_PROCESS
)

:: Small delay to let Windows release file locks
timeout /t 1 /nobreak >nul

set RETRY=0
:RETRY_MOVE
if exist %TARGET% (
    move /y %TARGET% %BACKUP% >nul 2>&1
)
move /y %UPDATE% %TARGET% >nul 2>&1

if not exist %TARGET% (
    set /a RETRY+=1
    if %RETRY% lss 10 (
        timeout /t 1 /nobreak >nul
        goto RETRY_MOVE
    )
)

:: Clean up backup
if exist %BACKUP% del /f /q %BACKUP% >nul 2>&1

:: Start updated application
start """" %TARGET%

:: Self-destruct updater script
(goto) 2>nul & del /f /q ""%~f0""
";

            File.WriteAllText(updaterBat, script);

            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{updaterBat}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = currentDir
            };

            Process.Start(startInfo);

            // Terminate current process immediately so all file handles and locks are released!
            Environment.Exit(0);
        }
    }
}
