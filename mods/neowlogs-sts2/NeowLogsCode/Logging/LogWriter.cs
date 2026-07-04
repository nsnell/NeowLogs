using System.Text.Json;
using NeowLogs.NeowLogsCode.Events;

namespace NeowLogs.NeowLogsCode;

public sealed class LogWriter
{
    private const long FlushIntervalMs = 1_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private StreamWriter? _writer;
    private long _lastFlushMs;
    public string CurrentPath { get; private set; } = "";

    public static string RunsRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeowLogs", "runs");

    public void Open(string runId)
    {
        Close();
        Directory.CreateDirectory(RunsRoot);
        CurrentPath = Path.Combine(RunsRoot, $"{runId}.jsonl");
        _writer = new StreamWriter(File.Open(CurrentPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite));
    }

    public void OpenAppend(string runId)
    {
        Close();
        Directory.CreateDirectory(RunsRoot);
        CurrentPath = Path.Combine(RunsRoot, $"{runId}.jsonl");
        _writer = new StreamWriter(File.Open(CurrentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
    }

    public void Close()
    {
        Flush();
        _writer?.Dispose();
        _writer = null;
    }

    public void Write(LogEvent ev)
    {
        if (_writer == null)
        {
            return;
        }

        _writer.WriteLine(JsonSerializer.Serialize(ev, JsonOptions));
        if (_clock.ElapsedMilliseconds - _lastFlushMs >= FlushIntervalMs)
        {
            Flush();
        }
    }

    public void Flush()
    {
        _writer?.Flush();
        _lastFlushMs = _clock.ElapsedMilliseconds;
    }
}
