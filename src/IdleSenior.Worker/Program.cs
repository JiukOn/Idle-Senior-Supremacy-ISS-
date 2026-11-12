using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using SAM.API;

namespace IdleSenior.Worker
{
    internal class Program
    {
        static int Main(string[] args)
        {
            if (args.Length == 0 || !long.TryParse(args[0], NumberStyles.Any, CultureInfo.InvariantCulture, out long appId) || appId <= 0)
            {
                Console.Error.WriteLine("[Worker] Uso: IdleSenior.Worker <AppId> [ParentPid]");
                return 1;
            }

            int parentPid = 0;
            if (args.Length >= 2)
            {
                int.TryParse(args[1], out parentPid);
            }

            SAM.API.Client client;
            try
            {
                client = new SAM.API.Client();
                client.Initialize(appId);

                // Actively bind this session to the logged-in user and request user stats from Steam servers
                if (client.SteamUser != null && client.SteamUserStats != null)
                {
                    ulong steamId = client.SteamUser.GetSteamId();
                    if (steamId > 0)
                    {
                        client.SteamUserStats.RequestUserStats(steamId);
                        Console.WriteLine($"[Worker] SteamUser {steamId} autenticado. Status In-Game requisitado.");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Worker] Falha ao inicializar idle para AppId {appId}: {ex.Message}");
                Console.Error.WriteLine("[Worker] Verifique se a Steam está aberta e logada.");
                return 1;
            }

            Console.WriteLine($"[Worker] Idle iniciado com sucesso: AppId {appId}");
            Console.Out.Flush();

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            // Listen for graceful stop command or stdin closure from parent UI process
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        string? line = await Console.In.ReadLineAsync();
                        if (line == null || line.Trim().Equals("STOP", StringComparison.OrdinalIgnoreCase))
                        {
                            cts.Cancel();
                            break;
                        }
                    }
                }
                catch
                {
                    cts.Cancel();
                }
            });

            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    if (parentPid > 0 && !IsProcessAlive(parentPid))
                    {
                        Console.WriteLine($"[Worker] Processo pai ({parentPid}) encerrado. Finalizando worker...");
                        break;
                    }

                    try
                    {
                        client.RunCallbacks(false);
                    }
                    catch { }

                    cts.Token.WaitHandle.WaitOne(1000);
                }
            }
            finally
            {
                try { client.Dispose(); } catch { }
                Console.WriteLine($"[Worker] Idle encerrado: AppId {appId}");
            }

            return 0;
        }

        private static bool IsProcessAlive(int pid)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                return !proc.HasExited;
            }
            catch
            {
                return false;
            }
        }
    }
}
