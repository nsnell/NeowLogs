using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace NeowLogs.NeowLogsCode;

[ModInitializer(nameof(Initialize))]
public partial class NeowLogsMod : Node
{
    public const string ModId = "NeowLogs";
    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } = new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    internal static readonly LogWriter Writer = new();
    internal static readonly StatsAccumulator Stats = new();
    internal static readonly EventRecorder Recorder = new(Writer, Stats);

    public static void Initialize()
    {
        Logger.Warn("NeowLogs initializing.");
        var harmony = new Harmony(ModId);
        var diagnosticsPath = AssemblyDiagnostics.WriteSnapshot();
        Logger.Warn($"NeowLogs wrote method diagnostics to {diagnosticsPath}");
        RuntimePatchRegistry.Install(harmony);
        Logger.Warn("NeowLogs ready. The meter/log will start when a run starts or loads.");
    }
}
