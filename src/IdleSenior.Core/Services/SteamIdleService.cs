using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace IdleSenior.Core.Services
{
    [Flags]
    public enum IdlePurpose
    {
        None = 0,
        Hours = 1,
        Cards = 2
    }

    public class SteamIdleService : IDisposable
    {
        public const int MaxSteamSimultaneousGames = 32;

        private readonly ConcurrentDictionary<int, Process> _activeWorkers = new();
        private readonly ConcurrentDictionary<int, byte> _intentionalStops = new();
        private readonly ConcurrentDictionary<int, DateTime> _startTimes = new();
        private readonly ConcurrentDictionary<int, IdlePurpose> _purposes = new();
        private bool _disposed;

        /// <summary>
        /// Fired when a worker stops. Parameters: (appId, wasUnexpected, errorMessage).
        /// </summary>
        public event Action<int, bool, string?>? WorkerStopped;

        public SteamIdleService()
        {
            CleanupStaleTempDirs();
        }

        public bool IsAnyIdling => !_activeWorkers.IsEmpty;

        public int ActiveWorkerCount => _activeWorkers.Count;

        public IReadOnlyCollection<int> GetActiveAppIds() => _activeWorkers.Keys.ToList();

        public IReadOnlyCollection<int> GetActiveAppIds(IdlePurpose purpose) =>
            _activeWorkers.Keys
                .Where(id => _purposes.TryGetValue(id, out var p) && p.HasFlag(purpose))
                .ToList();

        public bool StartIdle(int appId) => StartIdle(appId, IdlePurpose.Hours, out _);

        public bool StartIdle(int appId, out string? errorMessage) =>
            StartIdle(appId, IdlePurpose.Hours, out errorMessage);

        public bool StartIdle(int appId, IdlePurpose purpose, out string? errorMessage)
        {
            errorMessage = null;

            if (_activeWorkers.TryGetValue(appId, out var existing))
            {
                if (!existing.HasExited)
                {
                    _purposes.AddOrUpdate(appId, purpose, (_, cur) => cur | purpose);
                    return true;
                }
                _activeWorkers.TryRemove(appId, out _);
                _startTimes.TryRemove(appId, out _);
                _purposes.TryRemove(appId, out _);
            }

            if (_activeWorkers.Count >= MaxSteamSimultaneousGames)
            {
                errorMessage = $"Limite da Steam atingido ({MaxSteamSimultaneousGames} jogos simultâneos). Pare algum jogo antes de iniciar outro.";
                Debug.WriteLine($"[SteamIdleService] {errorMessage}");
                return false;
            }

            // 1. Check if Steam is running
            if (Process.GetProcessesByName("steam").Length == 0)
            {
                errorMessage = "Steam não está em execução. Abra o cliente Steam e faça login.";
                Debug.WriteLine($"[SteamIdleService] {errorMessage}");
                return false;
            }

            // 2. Find worker executable
            string workerPath = FindWorkerPath();
            if (string.IsNullOrEmpty(workerPath) || !File.Exists(workerPath))
            {
                errorMessage = "Executável IdleSenior.Worker.exe não encontrado.";
                Debug.WriteLine($"[SteamIdleService] {errorMessage}");
                return false;
            }

            _intentionalStops.TryRemove(appId, out _);

            var startInfo = new ProcessStartInfo
            {
                FileName = workerPath,
                Arguments = $"{appId} {Environment.ProcessId}",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(workerPath) ?? "",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            try
            {
                var process = Process.Start(startInfo);
                if (process == null)
                {
                    errorMessage = $"Não foi possível iniciar o processo Worker para o AppId {appId}.";
                    return false;
                }

                int workerPid = process.Id;
                var errorBuffer = new StringBuilder();

                _activeWorkers[appId] = process;
                _startTimes[appId] = DateTime.UtcNow;
                _purposes[appId] = purpose;
                process.EnableRaisingEvents = true;

                process.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null) Debug.WriteLine(e.Data);
                };

                process.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        Debug.WriteLine($"[Worker ERR] {e.Data}");
                        lock (errorBuffer)
                        {
                            if (errorBuffer.Length < 1024)
                                errorBuffer.AppendLine(e.Data);
                        }
                    }
                };

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                process.Exited += (s, e) =>
                {
                    _activeWorkers.TryRemove(appId, out _);
                    _startTimes.TryRemove(appId, out _);
                    _purposes.TryRemove(appId, out _);
                    bool intentional = _intentionalStops.TryRemove(appId, out _);

                    int exitCode = 0;
                    try { exitCode = process.ExitCode; } catch { }

                    // Fallback cleanup of worker temp folder if still present
                    CleanupWorkerTempDir(appId, workerPid);

                    string? errText;
                    lock (errorBuffer)
                    {
                        errText = errorBuffer.Length > 0 ? errorBuffer.ToString().Trim() : null;
                    }

                    bool unexpected = !intentional && exitCode != 0;
                    try
                    {
                        WorkerStopped?.Invoke(appId, unexpected || !intentional, errText);
                    }
                    catch { }

                    try { process.Dispose(); } catch { }
                };

                return true;
            }
            catch (Exception ex)
            {
                errorMessage = $"Falha ao iniciar Worker para AppId {appId}: {ex.Message}";
                Debug.WriteLine($"[SteamIdleService] {errorMessage}");
                return false;
            }
        }

        private string FindWorkerPath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string path = Path.Combine(baseDir, "IdleSenior.Worker.exe");
            if (File.Exists(path)) return path;

            string current = baseDir;
            for (int i = 0; i < 8; i++)
            {
                string[] possiblePaths = {
                    Path.Combine(current, "src", "IdleSenior.Worker", "bin", "Debug", "net9.0", "IdleSenior.Worker.exe"),
                    Path.Combine(current, "src", "IdleSenior.Worker", "bin", "Release", "net9.0", "IdleSenior.Worker.exe"),
                    Path.Combine(current, "IdleSenior.Worker", "bin", "Debug", "net9.0", "IdleSenior.Worker.exe"),
                    Path.Combine(current, "IdleSenior.Worker", "bin", "Release", "net9.0", "IdleSenior.Worker.exe")
                };

                foreach (var p in possiblePaths)
                {
                    if (File.Exists(p)) return p;
                }

                current = Path.GetDirectoryName(current) ?? "";
                if (string.IsNullOrEmpty(current)) break;
            }

            return string.Empty;
        }

        public void StopIdle(int appId)
        {
            _intentionalStops[appId] = 1;
            _startTimes.TryRemove(appId, out _);
            _purposes.TryRemove(appId, out _);

            if (_activeWorkers.TryRemove(appId, out var process))
            {
                int workerPid = 0;
                try { workerPid = process.Id; } catch { }

                try
                {
                    if (!process.HasExited)
                    {
                        // 1. Request graceful stop via stdin so Worker runs SteamAPI.Shutdown()
                        try
                        {
                            process.StandardInput.WriteLine("STOP");
                            process.StandardInput.Flush();
                            process.StandardInput.Close();
                        }
                        catch { }

                        // 2. Wait up to 1.5s for clean shutdown before forcing Kill
                        if (!process.WaitForExit(1500))
                        {
                            process.Kill(entireProcessTree: true);
                        }
                    }
                }
                catch { }
                finally
                {
                    if (workerPid > 0)
                        CleanupWorkerTempDir(appId, workerPid);

                    try { process.Dispose(); } catch { }
                }
            }
        }

        public void StopIdle(int appId, IdlePurpose purpose)
        {
            var remaining = _purposes.AddOrUpdate(appId, IdlePurpose.None, (_, cur) => cur & ~purpose);
            if (remaining == IdlePurpose.None)
            {
                StopIdle(appId);
            }
        }

        public void StopByPurpose(IdlePurpose purpose)
        {
            foreach (var appId in GetActiveAppIds(purpose))
            {
                StopIdle(appId, purpose);
            }
        }

        public void StopAll()
        {
            foreach (var appId in _activeWorkers.Keys.ToList())
            {
                StopIdle(appId);
            }
        }

        public bool IsIdling(int appId) =>
            _activeWorkers.TryGetValue(appId, out var proc) && !proc.HasExited;

        public bool IsIdling(int appId, IdlePurpose purpose) =>
            IsIdling(appId) && _purposes.TryGetValue(appId, out var p) && p.HasFlag(purpose);

        public DateTime? GetStartTime(int appId) =>
            _startTimes.TryGetValue(appId, out var dt) ? dt : null;

        private static void CleanupWorkerTempDir(int appId, int workerPid)
        {
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), $"ISS_{appId}_{workerPid}");
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch { }
        }

        private static void CleanupStaleTempDirs()
        {
            try
            {
                string tempRoot = Path.GetTempPath();
                foreach (var dir in Directory.EnumerateDirectories(tempRoot, "ISS_*"))
                {
                    string folderName = Path.GetFileName(dir);
                    var parts = folderName.Split('_');
                    // Format: ISS_{appId}_{pid}
                    if (parts.Length == 3 && int.TryParse(parts[2], out int pid))
                    {
                        bool alive = false;
                        try
                        {
                            using var p = Process.GetProcessById(pid);
                            alive = !p.HasExited;
                        }
                        catch { alive = false; }

                        if (!alive)
                        {
                            try { Directory.Delete(dir, recursive: true); } catch { }
                        }
                    }
                }
            }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopAll();
        }
    }
}
