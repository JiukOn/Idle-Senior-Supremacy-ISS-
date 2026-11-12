using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;
using IdleSenior.Core.Models;
using IdleSenior.Core.Services;

namespace IdleSenior.UI.Services
{
    public class ScheduledAchievementEntry
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int Order { get; set; }
        public double TargetOffsetSeconds { get; set; }
        public string? PrerequisiteId { get; set; }
        public string PrerequisiteHint { get; set; } = string.Empty;
        public bool Unlocked { get; set; }

        public TimeSpan TargetOffset => TimeSpan.FromSeconds(TargetOffsetSeconds);
    }

    public class PersistedAchievementSchedule
    {
        public int AppId { get; set; }
        public string GameName { get; set; } = string.Empty;
        public string ProfileName { get; set; } = "Median";
        public double ElapsedSeconds { get; set; }
        public double TotalTargetSeconds { get; set; }
        public DateTime LastUpdatedUtc { get; set; }
        public List<ScheduledAchievementEntry> Entries { get; set; } = new();
    }

    /// <summary>
    /// Coordinates simultaneous Hours Farming (IdleSenior.Worker) and timed, prerequisite-aware
    /// Achievement Unlocking (IdleSenior.AchievementWorker) with disk persistence for pause/resume across days.
    /// </summary>
    public class OrganicAchievementSchedulerService
    {
        private readonly SteamIdleService _idleService;
        private readonly SteamAchievementService _achievementService;
        private readonly AppSettingsService _settingsService;
        private readonly DispatcherTimer _tickTimer;

        private static readonly string SchedulesDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IdleSeniorSupremacy",
            "schedules");

        private PersistedAchievementSchedule? _activeSchedule;
        private bool _isRunning;
        private bool _unlockInFlight;
        private int _secondsSinceLastPersist;
        private int _secondsSinceLastStatsRecord;

        public int ActiveAppId => _activeSchedule?.AppId ?? 0;
        public string ActiveGameName => _activeSchedule?.GameName ?? string.Empty;
        public bool IsRunning => _isRunning;

        public TimeSpan ElapsedTime =>
            TimeSpan.FromSeconds(_activeSchedule?.ElapsedSeconds ?? 0);

        public TimeSpan TotalTargetDuration =>
            TimeSpan.FromSeconds(_activeSchedule?.TotalTargetSeconds ?? 0);

        public int UnlockedInScheduleCount =>
            _activeSchedule?.Entries.Count(e => e.Unlocked) ?? 0;

        public int TotalInScheduleCount =>
            _activeSchedule?.Entries.Count ?? 0;

        public ScheduledAchievementEntry? NextPendingEntry =>
            _activeSchedule?.Entries
                .Where(e => !e.Unlocked)
                .OrderBy(e => e.TargetOffsetSeconds)
                .ThenBy(e => e.Order)
                .FirstOrDefault();

        public event Action? StateChanged;
        public event Action<int, string, DateTime>? AchievementUnlocked;
        public event Action<int, string>? ScheduleCompleted;

        public OrganicAchievementSchedulerService(
            SteamIdleService idleService,
            SteamAchievementService achievementService,
            AppSettingsService settingsService)
        {
            _idleService = idleService;
            _achievementService = achievementService;
            _settingsService = settingsService;

            _tickTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _tickTimer.Tick += OnTickAsync;

            _idleService.WorkerStopped += OnWorkerStopped;
        }

        private void OnWorkerStopped(int appId, bool wasUnexpected, string? errorMessage)
        {
            // If the worker for the active game crashed unexpectedly while the scheduler is running, restart it
            if (_isRunning && _activeSchedule != null && _activeSchedule.AppId == appId && wasUnexpected)
            {
                Debug.WriteLine($"[OrganicScheduler] Worker for {appId} stopped unexpectedly ({errorMessage}); restarting idle worker.");
                _idleService.StartIdle(appId);
            }
        }

        /// <summary>
        /// Checks if there is a persisted, unfinished schedule for <paramref name="appId"/> on disk.
        /// If found, applies its scheduled offsets and order back onto <paramref name="achievements"/>.
        /// </summary>
        public PersistedAchievementSchedule? TryRestoreSavedSchedule(int appId, List<AchievementModel> achievements)
        {
            PersistedAchievementSchedule? schedule = null;

            if (_activeSchedule != null && _activeSchedule.AppId == appId)
            {
                schedule = _activeSchedule;
            }
            else
            {
                string path = GetScheduleFilePath(appId);
                if (File.Exists(path))
                {
                    try
                    {
                        string json = File.ReadAllText(path);
                        schedule = JsonSerializer.Deserialize<PersistedAchievementSchedule>(json);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[OrganicScheduler] Failed to read saved schedule for {appId}: {ex.Message}");
                    }
                }
            }

            if (schedule == null || schedule.Entries.Count == 0)
                return null;

            // Sync with real achievement status and apply schedule markers onto models
            var achMap = achievements.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var entry in schedule.Entries)
            {
                if (achMap.TryGetValue(entry.Id, out var model))
                {
                    if (model.IsAchieved)
                        entry.Unlocked = true;

                    model.SuggestedOrder = entry.Order;
                    model.PrerequisiteId = entry.PrerequisiteId;
                    model.PrerequisiteHint = entry.PrerequisiteHint;
                    model.ScheduledOffset = TimeSpan.FromSeconds(entry.TargetOffsetSeconds);
                }
            }

            // If all entries in the saved schedule are already unlocked, clean it up
            if (schedule.Entries.All(e => e.Unlocked))
            {
                DeleteSavedScheduleFile(appId);
                return null;
            }

            if (_activeSchedule == null)
                _activeSchedule = schedule;

            return schedule;
        }

        /// <summary>
        /// Creates and persists a new schedule from the generated timeline on <paramref name="achievements"/>.
        /// </summary>
        public PersistedAchievementSchedule CommitSchedule(
            int appId,
            string gameName,
            string profileName,
            List<AchievementModel> achievements)
        {
            if (_isRunning && _activeSchedule?.AppId != appId)
            {
                Pause();
            }

            var scheduledModels = achievements
                .Where(a => !a.IsAchieved && !a.IsUnobtainable && a.ScheduledOffset.HasValue && a.SuggestedOrder > 0)
                .OrderBy(a => a.SuggestedOrder)
                .ToList();

            var schedule = new PersistedAchievementSchedule
            {
                AppId = appId,
                GameName = gameName,
                ProfileName = profileName,
                ElapsedSeconds = 0,
                TotalTargetSeconds = scheduledModels.LastOrDefault()?.ScheduledOffset?.TotalSeconds ?? 0,
                LastUpdatedUtc = DateTime.UtcNow,
                Entries = scheduledModels.Select(a => new ScheduledAchievementEntry
                {
                    Id = a.Id,
                    Name = a.Name,
                    Order = a.SuggestedOrder,
                    TargetOffsetSeconds = a.ScheduledOffset!.Value.TotalSeconds,
                    PrerequisiteId = a.PrerequisiteId,
                    PrerequisiteHint = a.PrerequisiteHint,
                    Unlocked = false
                }).ToList()
            };

            _activeSchedule = schedule;
            SaveScheduleToDisk(schedule);
            StateChanged?.Invoke();
            return schedule;
        }

        /// <summary>
        /// Starts or resumes the simultaneous Hours + Achievements farm for the committed schedule.
        /// </summary>
        public bool StartOrResume(int appId, string gameName, string profileName, List<AchievementModel> achievements)
        {
            if (_activeSchedule == null || _activeSchedule.AppId != appId || _activeSchedule.Entries.All(e => e.Unlocked))
            {
                CommitSchedule(appId, gameName, profileName, achievements);
            }

            if (_activeSchedule == null || _activeSchedule.Entries.Count == 0)
                return false;

            // Start the hours farming worker so the game is actively running on Steam
            if (!_idleService.IsIdling(appId))
            {
                bool workerStarted = _idleService.StartIdle(appId);
                if (!workerStarted)
                    return false;
            }

            _secondsSinceLastPersist = 0;
            _secondsSinceLastStatsRecord = 0;
            _isRunning = true;
            _tickTimer.Start();
            SaveScheduleToDisk(_activeSchedule);
            StateChanged?.Invoke();
            return true;
        }

        /// <summary>
        /// Pauses the active schedule and stops the game worker, preserving progress on disk.
        /// </summary>
        public void Pause()
        {
            if (!_isRunning && _activeSchedule == null) return;

            _isRunning = false;
            _tickTimer.Stop();

            if (_activeSchedule != null)
            {
                _idleService.StopIdle(_activeSchedule.AppId);
                SaveScheduleToDisk(_activeSchedule);
            }

            StateChanged?.Invoke();
        }

        /// <summary>
        /// Stops the schedule and deletes its saved state file so a fresh one can be built.
        /// </summary>
        public void ResetSchedule(int appId)
        {
            if (_activeSchedule != null && _activeSchedule.AppId == appId)
            {
                if (_isRunning)
                {
                    _isRunning = false;
                    _tickTimer.Stop();
                    _idleService.StopIdle(appId);
                }
                _activeSchedule = null;
            }

            DeleteSavedScheduleFile(appId);
            StateChanged?.Invoke();
        }

        /// <summary>
        /// Immediately unlocks the next pending achievement in the queue and advances the timer to its offset.
        /// </summary>
        public async Task<bool> UnlockNextImmediatelyAsync()
        {
            if (_activeSchedule == null || _unlockInFlight) return false;
            var next = NextPendingEntry;
            if (next == null) return false;

            if (_activeSchedule.ElapsedSeconds < next.TargetOffsetSeconds)
                _activeSchedule.ElapsedSeconds = next.TargetOffsetSeconds;

            return await TryUnlockDueEntriesAsync();
        }

        private async void OnTickAsync(object? sender, EventArgs e)
        {
            if (!_isRunning || _activeSchedule == null) return;

            _activeSchedule.ElapsedSeconds += 1.0;
            _secondsSinceLastPersist++;
            _secondsSinceLastStatsRecord++;

            // Record 1 minute of idled hours every 60 seconds
            if (_secondsSinceLastStatsRecord >= 60)
            {
                _secondsSinceLastStatsRecord = 0;
                _settingsService.AddIdledStats(minutes: 1, cardsDropped: 0);
            }

            // Persist schedule progress every 30 seconds
            if (_secondsSinceLastPersist >= 30)
            {
                _secondsSinceLastPersist = 0;
                SaveScheduleToDisk(_activeSchedule);
            }

            StateChanged?.Invoke();

            // Check if any achievement is due
            var next = NextPendingEntry;
            if (next != null && _activeSchedule.ElapsedSeconds >= next.TargetOffsetSeconds && !_unlockInFlight)
            {
                await TryUnlockDueEntriesAsync();
            }
        }

        private async Task<bool> TryUnlockDueEntriesAsync()
        {
            if (_activeSchedule == null || _unlockInFlight) return false;

            var schedule = _activeSchedule;
            var entryById = schedule.Entries.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);

            // Find all entries whose target offset has arrived AND whose prerequisite is satisfied
            var dueEntries = schedule.Entries
                .Where(e => !e.Unlocked && e.TargetOffsetSeconds <= schedule.ElapsedSeconds)
                .Where(e => string.IsNullOrEmpty(e.PrerequisiteId) ||
                            !entryById.TryGetValue(e.PrerequisiteId, out var prereq) ||
                            prereq.Unlocked)
                .OrderBy(e => e.Order)
                .Take(2) // At most 2 simultaneous unlocks in a single worker invocation
                .ToList();

            if (dueEntries.Count == 0) return false;

            _unlockInFlight = true;
            try
            {
                var payloadModels = dueEntries.Select(e => new AchievementModel
                {
                    Id = e.Id,
                    Name = e.Name,
                    IsAchieved = true
                }).ToList();

                int appId = schedule.AppId;
                bool success = await Task.Run(() => _achievementService.SaveAchievements(appId, payloadModels));

                if (success)
                {
                    DateTime now = DateTime.Now;
                    foreach (var entry in dueEntries)
                    {
                        entry.Unlocked = true;
                        AchievementUnlocked?.Invoke(appId, entry.Id, now);
                    }

                    SaveScheduleToDisk(schedule);
                    StateChanged?.Invoke();

                    // Check if the entire schedule has been completed
                    if (schedule.Entries.All(e => e.Unlocked))
                    {
                        string completedGame = schedule.GameName;
                        _isRunning = false;
                        _tickTimer.Stop();
                        _idleService.StopIdle(appId);
                        DeleteSavedScheduleFile(appId);
                        StateChanged?.Invoke();
                        ScheduleCompleted?.Invoke(appId, completedGame);
                    }

                    return true;
                }
                else
                {
                    // Back off by 45 seconds so we don't spam the worker every second if Steam is busy
                    foreach (var entry in dueEntries)
                        entry.TargetOffsetSeconds = schedule.ElapsedSeconds + 45;
                    return false;
                }
            }
            finally
            {
                _unlockInFlight = false;
            }
        }

        private static string GetScheduleFilePath(int appId) =>
            Path.Combine(SchedulesDir, $"schedule_{appId}.json");

        private static void SaveScheduleToDisk(PersistedAchievementSchedule schedule)
        {
            try
            {
                Directory.CreateDirectory(SchedulesDir);
                schedule.LastUpdatedUtc = DateTime.UtcNow;
                string json = JsonSerializer.Serialize(schedule, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(GetScheduleFilePath(schedule.AppId), json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OrganicScheduler] Failed to save schedule: {ex.Message}");
            }
        }

        private static void DeleteSavedScheduleFile(int appId)
        {
            try
            {
                string path = GetScheduleFilePath(appId);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }
    }
}
