using System.Reflection;
using HarmonyLib;

namespace NeowLogs.NeowLogsCode;

public static class RuntimePatchRegistry
{
    private static readonly PatchSpec[] PatchSpecs =
    [
        new("damage_dealt", ["MegaCrit.Sts2.Core.Combat.History.CombatHistory"], ["CreatureAttacked"]),
        new("damage_observed", ["MegaCrit.Sts2.Core.Commands.CreatureCmd"], ["Damage", "LoseHp", "LoseHP"]),
        new("damage_taken", ["MegaCrit.Sts2.Core.Combat.History.CombatHistory"], ["DamageReceived"]),
        new("block_gained", ["MegaCrit.Sts2.Core.Combat.History.CombatHistory"], ["BlockGained"]),
        new("card_played", ["MegaCrit.Sts2.Core.Combat.History.CombatHistory"], ["CardPlayStarted"]),
        new("debuff_applied", ["MegaCrit.Sts2.Core.Commands.PowerCmd", "MegaCrit.Sts2.Core.Commands.ApplyPowerCommand", "MegaCrit.Sts2.Commands.PowerCmd", "MegaCrit.Sts2.Commands.ApplyPowerCommand"], ["Execute", "Run", "Process", "Apply"]),
        new("healing_done", ["MegaCrit.Sts2.Core.Commands.CreatureCmd"], ["Heal"]),
        // Doom kills drop HP to 0 with no attack event, and CombatHistory has no death or turn
        // methods (verified against sts2.dll: it only declares CardPlayStarted, CreatureAttacked,
        // DamageReceived, BlockGained). The real doom-death signal is the Hook dispatcher
        // AfterDiedToDoom(combatState, IReadOnlyList<Creature>); patch it to attribute the kill.
        new("doom_died", ["MegaCrit.Sts2.Core.Hooks.Hook"], ["AfterDiedToDoom"])
    ];

    public static void Install(Harmony harmony)
    {
        LifecyclePatches.Install(harmony);

        var postfix = new HarmonyMethod(typeof(RuntimePatchRegistry).GetMethod(nameof(GenericPostfix), BindingFlags.NonPublic | BindingFlags.Static));
        var patched = 0;

        foreach (var spec in PatchSpecs)
        {
            foreach (var typeName in spec.TypeNames)
            {
                var type = AccessTools.TypeByName(typeName);
                if (type == null)
                {
                    continue;
                }

                foreach (var methodName in spec.MethodNames)
                {
                    foreach (var method in FindPatchableMethods(type, methodName))
                    {
                        try
                        {
                            EventTap.RegisterMethodEventType(method, spec.EventType);
                            harmony.Patch(method, postfix: postfix);
                            patched += 1;
                            NeowLogsMod.Logger.Warn($"NeowLogs patched {type.FullName}.{method.Name} for {spec.EventType}");
                        }
                        catch (Exception ex)
                        {
                            NeowLogsMod.Logger.Warn($"NeowLogs skipped {type.FullName}.{method.Name}: {ex.GetType().Name}: {ex.Message}");
                        }
                    }
                }
            }
        }

        if (patched == 0)
        {
            NeowLogsMod.Logger.Warn("NeowLogs did not find combat command targets. Decompile sts2.dll and update RuntimePatchRegistry target names.");
        }
    }

    private static void GenericPostfix(MethodBase __originalMethod, object? __instance, object[] __args)
    {
        try
        {
            EventTap.RecordFromCommand(__originalMethod, __instance, __args);
        }
        catch (Exception ex)
        {
            NeowLogsMod.Logger.Warn($"NeowLogs command tap failed: {ex.Message}");
        }
    }

    private static IEnumerable<MethodBase> FindPatchableMethods(Type type, string methodName)
    {
        return AccessTools.GetDeclaredMethods(type)
            .Where(method => method.Name == methodName)
            .Where(method => !method.ContainsGenericParameters)
            .Where(method => !method.IsAbstract);
    }

    private sealed record PatchSpec(string EventType, string[] TypeNames, string[] MethodNames);
}
