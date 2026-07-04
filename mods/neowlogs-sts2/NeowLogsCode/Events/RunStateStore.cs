using System.Text.Json;

namespace NeowLogs.NeowLogsCode.Events;

public sealed class RunStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static string StateRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeowLogs", "state");
    public static string CompletedRoot => Path.Combine(StateRoot, "completed-runs");
    public static string ActivePath => Path.Combine(StateRoot, "active-run.json");

    public RunCheckpoint? LoadActive()
    {
        if (!File.Exists(ActivePath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RunCheckpoint>(File.ReadAllText(ActivePath));
        }
        catch (Exception ex)
        {
            NeowLogsMod.Logger.Warn($"NeowLogs could not read active checkpoint: {ex.Message}");
            return null;
        }
    }

    public void SaveActive(RunCheckpoint checkpoint)
    {
        try
        {
            Directory.CreateDirectory(StateRoot);
            checkpoint.UpdatedAtUtc = DateTime.UtcNow;
            File.WriteAllText(ActivePath, JsonSerializer.Serialize(checkpoint, JsonOptions));
        }
        catch (Exception ex)
        {
            NeowLogsMod.Logger.Warn($"NeowLogs could not save active checkpoint: {ex.Message}");
        }
    }

    public void CompleteActive(string runId)
    {
        try
        {
            if (!File.Exists(ActivePath))
            {
                return;
            }

            Directory.CreateDirectory(CompletedRoot);
            var destination = Path.Combine(CompletedRoot, $"{runId}.state.json");
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            File.Move(ActivePath, destination);
        }
        catch (Exception ex)
        {
            NeowLogsMod.Logger.Warn($"NeowLogs could not complete active checkpoint: {ex.Message}");
        }
    }

    public void ClearActive()
    {
        try
        {
            if (File.Exists(ActivePath))
            {
                File.Delete(ActivePath);
            }
        }
        catch (Exception ex)
        {
            NeowLogsMod.Logger.Warn($"NeowLogs could not clear active checkpoint: {ex.Message}");
        }
    }
}
