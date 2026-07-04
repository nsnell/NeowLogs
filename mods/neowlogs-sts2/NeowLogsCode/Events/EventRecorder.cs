using System.Diagnostics;
using System.Text.Json;
using NeowLogs.NeowLogsCode.Events;

namespace NeowLogs.NeowLogsCode;

public sealed class EventRecorder(LogWriter writer, StatsAccumulator stats)
{
    private const long CheckpointIntervalMs = 2_000;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly RunStateStore _stateStore = new();

    public string RunId { get; private set; } = "";
    public string RunKey { get; private set; } = "";
    public string? CombatId { get; private set; }
    public int? Act { get; private set; }
    public int? Floor { get; private set; }
    public int? Turn { get; private set; }
    public bool HasActiveRun => !string.IsNullOrWhiteSpace(RunId);
    private bool _runEnded;
    private long _lastActTransitionMs;
    private long _lastCheckpointMs = -CheckpointIntervalMs;

    public bool TryResumeLatestRun()
    {
        if (!string.IsNullOrWhiteSpace(RunId))
        {
            return false;
        }

        if (TryResumeActiveCheckpoint())
        {
            return true;
        }

        if (!Directory.Exists(LogWriter.RunsRoot))
        {
            return false;
        }

        foreach (var file in Directory.GetFiles(LogWriter.RunsRoot, "*.jsonl").OrderByDescending(File.GetLastWriteTimeUtc))
        {
            var events = ReadEvents(file).ToArray();
            if (events.Length == 0 || events.Any(ev => ev.EventType == "run_ended"))
            {
                continue;
            }

            stats.Reset();
            Act = null;
            Floor = null;
            Turn = null;
            CombatId = null;
            foreach (var ev in events)
            {
                stats.Consume(ev);
                Act = ev.Act ?? Act;
                Floor = ev.Floor ?? Floor;
                Turn = ev.Turn ?? Turn;
                CombatId = ev.CombatId ?? CombatId;
            }

            RunId = Path.GetFileNameWithoutExtension(file);
            RunKey = $"legacy:{RunId}";
            _runEnded = false;
            _lastActTransitionMs = _clock.ElapsedMilliseconds;
            writer.OpenAppend(RunId);
            UiRuntime.Refresh(stats.Players);
            SaveCheckpoint(force: true);
            return true;
        }

        return false;
    }

    public void StartRun(Dictionary<string, object?> metadata)
    {
        if (!string.IsNullOrWhiteSpace(RunId))
        {
            return;
        }

        if (IsResumeLifecycle(metadata) && TryResumeActiveCheckpoint())
        {
            NeowLogsMod.Logger.Warn($"NeowLogs resumed active log: {writer.CurrentPath}");
            return;
        }

        RunId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        RunKey = BuildRunKey(metadata, RunId);
        metadata["run_key"] = RunKey;
        _runEnded = false;
        _lastActTransitionMs = _clock.ElapsedMilliseconds;
        writer.Open(RunId);
        Record("run_started", metadata: metadata);
    }

    public void StartNewRun(Dictionary<string, object?> metadata)
    {
        writer.Close();
        stats.Reset();
        RunId = "";
        RunKey = "";
        CombatId = null;
        Act = null;
        Floor = null;
        Turn = null;
        _runEnded = false;
        _stateStore.ClearActive();
        StartRun(metadata);
    }

    public void StartCombat(Dictionary<string, object?> metadata)
    {
        if (string.IsNullOrWhiteSpace(RunId))
        {
            StartRun(new Dictionary<string, object?>(metadata) { ["source"] = "combat_start" });
        }

        CombatId = ReadString(metadata, "combat_id") ?? $"combat-{_clock.ElapsedMilliseconds}";
        var nextAct = ReadInt(metadata, "act") ?? ReadInt(metadata, "Act") ?? Act;
        if (Act != null && nextAct != null && nextAct > Act)
        {
            UiRuntime.ShowActHighlight(Act.Value, stats.Players);
            Record("act_ended", amount: Act.Value, metadata: new Dictionary<string, object?>
            {
                ["act"] = Act.Value,
                ["next_act"] = nextAct.Value,
                ["source"] = "act_change"
            });
            _lastActTransitionMs = _clock.ElapsedMilliseconds;
        }
        Act = nextAct;
        Floor = ReadInt(metadata, "floor") ?? Floor;
        Turn = ReadInt(metadata, "turn") ?? 1;
        stats.StartCombat(CombatId);
        Record("combat_started", metadata: metadata);
    }

    public void EndCombat(Dictionary<string, object?> metadata)
    {
        Record("combat_ended", amount: ReadDouble(metadata, "turns_taken"), metadata: metadata);
        CombatId = null;
        writer.Flush();
        SaveCheckpoint(force: true);
    }

    public void Record(
        string eventType,
        string? actorPlayerId = null,
        string? actorName = null,
        string? targetId = null,
        string? targetName = null,
        double? amount = null,
        double? baseAmount = null,
        string? sourceType = null,
        string? sourceName = null,
        Dictionary<string, object?>? metadata = null)
    {
        if (string.IsNullOrWhiteSpace(RunId))
        {
            StartRun(new Dictionary<string, object?> { ["source"] = "lazy_start" });
        }

        if (metadata != null)
        {
            Turn = ReadInt(metadata, "turn")
                ?? ReadInt(metadata, "Turn")
                ?? ReadInt(metadata, "current_turn")
                ?? ReadInt(metadata, "combat_turn")
                ?? Turn;
        }

        var ev = new LogEvent
        {
            RunId = RunId,
            CombatId = CombatId,
            TimestampMs = _clock.ElapsedMilliseconds,
            Act = Act,
            Floor = Floor,
            Turn = Turn,
            ActorPlayerId = actorPlayerId,
            ActorName = actorName,
            TargetId = targetId,
            TargetName = targetName,
            EventType = eventType,
            Amount = amount,
            BaseAmount = baseAmount,
            SourceType = sourceType,
            SourceName = sourceName,
            Metadata = metadata ?? new Dictionary<string, object?>()
        };

        var boundaryEvent = IsBoundaryEvent(eventType);
        try
        {
            writer.Write(ev);
            if (boundaryEvent)
            {
                writer.Flush();
            }
        }
        catch (Exception ex)
        {
            NeowLogsMod.Logger.Warn($"NeowLogs failed to write event {eventType}: {ex.Message}");
        }

        try
        {
            stats.Consume(ev);
        }
        catch (Exception ex)
        {
            NeowLogsMod.Logger.Warn($"NeowLogs failed to accumulate event {eventType}: {ex.Message}");
        }

        try
        {
            UiRuntime.Refresh(stats.Players);
        }
        catch (Exception ex)
        {
            NeowLogsMod.Logger.Warn($"NeowLogs failed to refresh meter: {ex.Message}");
        }

        try
        {
            SaveCheckpoint(force: boundaryEvent);
        }
        catch (Exception ex)
        {
            NeowLogsMod.Logger.Warn($"NeowLogs failed to save checkpoint: {ex.Message}");
        }
    }

    public void RenamePlayer(string playerId, string name)
    {
        stats.RenamePlayer(playerId, name);
        Record("player_renamed", actorPlayerId: playerId, actorName: name, metadata: new Dictionary<string, object?>
        {
            ["player_id"] = playerId,
            ["display_name"] = name
        });
    }

    public void EndRun(Dictionary<string, object?> metadata)
    {
        if (_runEnded || string.IsNullOrWhiteSpace(RunId))
        {
            return;
        }

        var endedRunId = RunId;
        Record("run_ended", metadata: metadata);
        _runEnded = true;
        writer.Flush();
        writer.Close();
        _stateStore.CompleteActive(endedRunId);
        RunId = "";
        RunKey = "";
        CombatId = null;
        Act = null;
        Floor = null;
        Turn = null;
    }

    public bool IsLikelyActOpeningHeal(bool inCombat, double requested, double currentHp, double maxHp)
    {
        if (inCombat || maxHp <= 0 || requested <= 0)
        {
            return false;
        }

        // Act setup heals appear as large non-combat heals around run/act
        // transitions. Rest sites generally pass the smaller heal amount.
        if (requested >= maxHp)
        {
            return true;
        }

        var nearActTransition = _clock.ElapsedMilliseconds - _lastActTransitionMs < 10_000;
        return nearActTransition && currentHp >= maxHp;
    }

    private bool TryResumeActiveCheckpoint()
    {
        var checkpoint = _stateStore.LoadActive();
        if (checkpoint == null || string.IsNullOrWhiteSpace(checkpoint.RunId))
        {
            return false;
        }

        var logPath = string.IsNullOrWhiteSpace(checkpoint.LogPath)
            ? Path.Combine(LogWriter.RunsRoot, $"{checkpoint.RunId}.jsonl")
            : checkpoint.LogPath;
        if (!File.Exists(logPath))
        {
            _stateStore.ClearActive();
            return false;
        }

        if (ReadEvents(logPath).Any(ev => ev.EventType == "run_ended"))
        {
            _stateStore.CompleteActive(checkpoint.RunId);
            return false;
        }

        stats.Restore(checkpoint.Stats);
        RunId = checkpoint.RunId;
        RunKey = string.IsNullOrWhiteSpace(checkpoint.RunKey) ? $"checkpoint:{RunId}" : checkpoint.RunKey;
        Act = checkpoint.Act;
        Floor = checkpoint.Floor;
        Turn = checkpoint.Turn;
        CombatId = checkpoint.CombatId;
        _runEnded = false;
        _lastActTransitionMs = _clock.ElapsedMilliseconds;
        writer.OpenAppend(RunId);
        UiRuntime.Refresh(stats.Players);
        return true;
    }

    private void SaveCheckpoint(bool force = false)
    {
        if (string.IsNullOrWhiteSpace(RunId) || _runEnded)
        {
            return;
        }

        if (!force && _clock.ElapsedMilliseconds - _lastCheckpointMs < CheckpointIntervalMs)
        {
            return;
        }

        _lastCheckpointMs = _clock.ElapsedMilliseconds;
        _stateStore.SaveActive(new RunCheckpoint
        {
            RunId = RunId,
            RunKey = string.IsNullOrWhiteSpace(RunKey) ? $"run:{RunId}" : RunKey,
            LogPath = writer.CurrentPath,
            Act = Act,
            Floor = Floor,
            Turn = Turn,
            CombatId = CombatId,
            Stats = stats.CreateCheckpoint()
        });
    }

    private static bool IsBoundaryEvent(string eventType)
    {
        return eventType is "run_started"
            or "run_ended"
            or "combat_started"
            or "combat_ended"
            or "act_ended"
            or "player_renamed";
    }

    private static bool IsResumeLifecycle(Dictionary<string, object?> metadata)
    {
        var text = $"{ReadString(metadata, "controller_type")} {ReadString(metadata, "method")} {ReadString(metadata, "source")}".ToLowerInvariant();
        return text.Contains("loadrun")
            || text.Contains("continue")
            || text.Contains("resume");
    }

    private static string BuildRunKey(Dictionary<string, object?> metadata, string runId)
    {
        var parts = new List<string>();
        AddPart(parts, "seed", ReadStringAny(metadata, "seed", "Seed", "run_seed", "RunSeed"));
        AddPart(parts, "asc", ReadStringAny(metadata, "ascension", "Ascension", "ascension_level", "AscensionLevel"));
        AddPart(parts, "players", ReadStringAny(metadata, "player_count", "PlayerCount", "players", "Players"));
        AddPart(parts, "game", ReadStringAny(metadata, "game_version", "GameVersion"));
        return parts.Count == 0 ? $"run:{runId}" : string.Join("|", parts);
    }

    private static void AddPart(List<string> parts, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add($"{name}:{value.Trim()}");
        }
    }

    private static IEnumerable<LogEvent> ReadEvents(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            LogEvent? ev = null;
            try
            {
                ev = JsonSerializer.Deserialize<LogEvent>(line);
            }
            catch
            {
            }

            if (ev != null)
            {
                yield return ev;
            }
        }
    }

    public static string? ReadString(Dictionary<string, object?> data, string key)
    {
        return data.TryGetValue(key, out var value) ? value?.ToString() : null;
    }

    private static string? ReadStringAny(Dictionary<string, object?> data, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = ReadString(data, key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    public static int? ReadInt(Dictionary<string, object?> data, string key)
    {
        if (!data.TryGetValue(key, out var value) || value == null)
        {
            return null;
        }

        if (value is int i)
        {
            return i;
        }

        return int.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }

    public static double? ReadDouble(Dictionary<string, object?> data, string key)
    {
        if (!data.TryGetValue(key, out var value) || value == null)
        {
            return null;
        }

        if (value is double d)
        {
            return d;
        }

        if (value is float f)
        {
            return f;
        }

        if (value is int i)
        {
            return i;
        }

        return double.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }
}
