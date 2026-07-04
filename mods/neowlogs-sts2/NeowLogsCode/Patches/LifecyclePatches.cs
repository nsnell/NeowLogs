using System.Reflection;
using HarmonyLib;

namespace NeowLogs.NeowLogsCode;

// These string-based Harmony targets are intentionally permissive. StS2 is
// still moving in Early Access, so RuntimePatchRegistry logs missing targets
// instead of hard-failing the whole mod.
public static class LifecyclePatches
{
    public static int Install(Harmony harmony)
    {
        var patched = 0;
        var postfix = new HarmonyMethod(typeof(LifecyclePatches).GetMethod(nameof(Postfix)));
        foreach (var typeName in new[]
                 {
                     "MegaCrit.Sts2.Core.Gameplay.RunController",
                     "MegaCrit.Sts2.Gameplay.RunController",
                     "MegaCrit.Sts2.Core.Combat.CombatController",
                     "MegaCrit.Sts2.Combat.CombatController",
                     "MegaCrit.Sts2.Core.Combat.CombatManager",
                     "MegaCrit.Sts2.Core.Multiplayer.Game.Lobby.StartRunLobby",
                     "MegaCrit.Sts2.Core.Multiplayer.Game.Lobby.LoadRunLobby"
                 })
        {
            var type = AccessTools.TypeByName(typeName);
            if (type == null)
            {
                continue;
            }

            foreach (var methodName in new[] { "StartRun", "BeginRun", "BeginRunForAllPlayers", "BeginRunIfAllPlayersReady", "TryBeginRun", "StartCombat", "BeginCombat", "SetUpCombat", "EndCombat", "EndCombatInternal", "EndRun" })
            {
                var method = AccessTools.Method(type, methodName);
                if (method != null)
                {
                    harmony.Patch(method, postfix: postfix);
                    patched += 1;
                    NeowLogsMod.Logger.Warn($"NeowLogs patched lifecycle target {type.FullName}.{method.Name}");
                }
            }
        }

        if (patched == 0)
        {
            NeowLogsMod.Logger.Warn("NeowLogs did not find run/combat lifecycle targets. The mod will still load and create a log, but may need updated patch targets.");
        }

        return patched;
    }

    public static void Postfix(System.Reflection.MethodBase __originalMethod, object __instance, object[] __args)
    {
        try
        {
            RecordLifecycle(__originalMethod, __instance, __args);
        }
        catch (Exception ex)
        {
            NeowLogsMod.Logger.Warn($"NeowLogs lifecycle hook failed for {__originalMethod.DeclaringType?.FullName}.{__originalMethod.Name}: {ex.Message}");
        }
    }

    private static void RecordLifecycle(System.Reflection.MethodBase originalMethod, object instance, object[] args)
    {
        UiRuntime.EnsureAttached(instance as Godot.Node);

        var methodName = originalMethod.Name.ToLowerInvariant();
        var metadata = new Dictionary<string, object?>
        {
            ["controller_type"] = instance.GetType().FullName,
            ["method"] = originalMethod.Name,
            ["arg_count"] = args.Length
        };
        AddRunPosition(metadata, instance, args);
        AddRunIdentity(metadata, instance, args);

        if (methodName.Contains("startrun") || methodName.Contains("beginrun"))
        {
            NeowLogsMod.Recorder.StartRun(metadata);
        }
        else if (methodName.Contains("startcombat") || methodName.Contains("begincombat") || methodName.Contains("setupcombat"))
        {
            NeowLogsMod.Recorder.StartCombat(metadata);
        }
        else if (methodName.Contains("endcombat"))
        {
            NeowLogsMod.Recorder.EndCombat(metadata);
        }
        else if (methodName.Contains("endrun"))
        {
            NeowLogsMod.Recorder.EndRun(metadata);
        }
    }

    private static void AddRunPosition(Dictionary<string, object?> metadata, object instance, object[] args)
    {
        var values = args.Prepend(instance);
        var act = FirstInt(values, "Act", "CurrentAct", "ActNumber", "act", "actNumber");
        var floor = FirstInt(values, "Floor", "CurrentFloor", "FloorNumber", "floor", "floorNumber");
        if (act != null)
        {
            metadata["act"] = act;
        }
        if (floor != null)
        {
            metadata["floor"] = floor;
        }
    }

    private static void AddRunIdentity(Dictionary<string, object?> metadata, object instance, object[] args)
    {
        var values = args.Prepend(instance);
        var seed = FirstString(values, "Seed", "CurrentSeed", "RunSeed", "seed", "runSeed");
        var ascension = FirstInt(values, "Ascension", "AscensionLevel", "CurrentAscension", "ascension", "ascensionLevel");
        var playerCount = FirstInt(values, "PlayerCount", "NumPlayers", "PlayerCountReady", "playerCount");
        var gameVersion = FirstString(values, "GameVersion", "Version", "gameVersion");

        if (!string.IsNullOrWhiteSpace(seed))
        {
            metadata["seed"] = seed;
        }
        if (ascension != null)
        {
            metadata["ascension"] = ascension;
        }
        if (playerCount != null)
        {
            metadata["player_count"] = playerCount;
        }
        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            metadata["game_version"] = gameVersion;
        }
    }

    private static int? FirstInt(IEnumerable<object?> values, params string[] names)
    {
        foreach (var value in values)
        {
            if (value == null)
            {
                continue;
            }

            foreach (var name in names)
            {
                var raw = ReadMember(value, name);
                if (raw == null)
                {
                    continue;
                }
                if (raw is int i)
                {
                    return i;
                }
                if (int.TryParse(raw.ToString(), out var parsed))
                {
                    return parsed;
                }
            }
        }

        return null;
    }

    private static string? FirstString(IEnumerable<object?> values, params string[] names)
    {
        foreach (var value in values)
        {
            if (value == null)
            {
                continue;
            }

            foreach (var name in names)
            {
                var raw = ReadMember(value, name);
                if (raw == null)
                {
                    continue;
                }

                var text = raw.ToString();
                if (!string.IsNullOrWhiteSpace(text) && !text.StartsWith("MegaCrit.", StringComparison.Ordinal))
                {
                    return text;
                }
            }
        }

        return null;
    }

    private static object? ReadMember(object instance, string name)
    {
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
        var property = instance.GetType().GetProperty(name, flags);
        if (property != null && property.GetIndexParameters().Length == 0)
        {
            return property.GetValue(instance);
        }

        var field = instance.GetType().GetField(name, flags);
        return field?.GetValue(instance);
    }
}
