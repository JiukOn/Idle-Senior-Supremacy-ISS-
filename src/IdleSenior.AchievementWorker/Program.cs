using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using SAM.API;
using SAM.API.Callbacks;

namespace IdleSenior.AchievementWorker
{
    public class AchievementPayload
    {
        public int AppId { get; set; }
        public List<AchievementItemPayload> Achievements { get; set; } = new();
    }

    public class AchievementItemPayload
    {
        public string Id { get; set; } = string.Empty;
        public bool Achieved { get; set; }
    }

    internal class Program
    {
        static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("[AchievementWorker] Uso: IdleSenior.AchievementWorker <caminho_json_ou_inline>");
                return 1;
            }

            string input = args[0];
            string jsonContent;

            if (File.Exists(input))
            {
                try
                {
                    jsonContent = File.ReadAllText(input);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AchievementWorker] Falha ao ler arquivo de entrada: {ex.Message}");
                    return 1;
                }
            }
            else
            {
                jsonContent = input;
            }

            AchievementPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<AchievementPayload>(jsonContent, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AchievementWorker] JSON invalido: {ex.Message}");
                return 1;
            }

            if (payload == null || payload.AppId <= 0)
            {
                Console.Error.WriteLine("[AchievementWorker] Payload invalido ou AppId invalido.");
                return 1;
            }

            int appId = payload.AppId;
            Console.WriteLine($"[AchievementWorker] Inicializando SAM para AppId {appId} ({payload.Achievements.Count} conquistas)...");

            Client? client = null;
            try
            {
                client = new Client();
                client.Initialize(appId);

                if (client.SteamUser == null || client.SteamUserStats == null)
                {
                    Console.Error.WriteLine("[AchievementWorker] Falha: Interfaces do SteamUser ou SteamUserStats nao disponiveis.");
                    return 1;
                }

                ulong steamId;
                try
                {
                    steamId = client.SteamUser.GetSteamId();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AchievementWorker] Falha ao obter SteamId: {ex.Message}");
                    return 1;
                }

                bool statsReady = false;
                var cb = client.CreateAndRegisterCallback<UserStatsReceived>();
                cb.OnRun += data =>
                {
                    if (data.Result == 1 /* k_EResultOK */ &&
                        (data.GameId == (ulong)appId || data.GameId == 0))
                    {
                        statsReady = true;
                    }
                };

                client.SteamUserStats.RequestUserStats(steamId);

                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!statsReady && DateTime.UtcNow < deadline)
                {
                    try { client.RunCallbacks(false); } catch { break; }
                    Thread.Sleep(100);
                }

                if (!statsReady)
                {
                    Console.Error.WriteLine("[AchievementWorker] Timeout aguardando UserStatsReceived da Steam.");
                    return 1;
                }

                // Aplica cada uma das conquistas solicitadas
                foreach (var ach in payload.Achievements)
                {
                    client.SteamUserStats.SetAchievement(ach.Id, ach.Achieved);
                }

                // Grava as estatisticas na Valve
                bool stored = client.SteamUserStats.StoreStats();
                if (!stored)
                {
                    Console.Error.WriteLine("[AchievementWorker] StoreStats retornou falso.");
                    return 1;
                }

                Console.WriteLine("[AchievementWorker] Conquistas gravadas com sucesso no Steam!");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AchievementWorker] Erro inesperado: {ex.Message}");
                return 1;
            }
            finally
            {
                try { client?.Dispose(); } catch { }
            }
        }
    }
}
