using System.Reflection;

namespace NeowLogs.NeowLogsCode;

public static class AssemblyDiagnostics
{
    private static readonly string[] InterestingTypeTerms =
    [
        "Command", "Cmd", "Combat", "Damage", "Block", "Card", "Hook", "Power", "Creature", "Player", "Turn"
    ];

    private static readonly string[] InterestingMethodTerms =
    [
        "Attack", "Damage", "Block", "Play", "Card", "Power", "Apply", "Combat", "Turn", "Hp", "Health", "Lose", "Gain", "After"
    ];

    public static string WriteSnapshot()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeowLogs", "diagnostics");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, $"sts2-methods-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

        using var writer = new StreamWriter(File.Open(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite));
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().OrderBy(a => a.GetName().Name))
        {
            var assemblyName = assembly.GetName().Name ?? "";
            if (!assemblyName.Contains("sts2", StringComparison.OrdinalIgnoreCase) &&
                !assemblyName.Contains("BaseLib", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            writer.WriteLine($"# Assembly: {assembly.FullName}");
            foreach (var type in SafeTypes(assembly).Where(IsInterestingType).OrderBy(t => t.FullName))
            {
                writer.WriteLine($"TYPE {type.FullName}");
                foreach (var method in SafeMethods(type).Where(IsInterestingMethod).OrderBy(m => m.Name))
                {
                    writer.WriteLine($"  {FormatMethod(method)}");
                }
            }
        }

        return path;
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null)!;
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<MethodInfo> SafeMethods(Type type)
    {
        try
        {
            return type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        }
        catch
        {
            return [];
        }
    }

    private static bool IsInterestingType(Type type)
    {
        var name = type.FullName ?? type.Name;
        return InterestingTypeTerms.Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsInterestingMethod(MethodInfo method)
    {
        var name = method.Name;
        return InterestingMethodTerms.Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatMethod(MethodInfo method)
    {
        var generic = method.ContainsGenericParameters ? " generic" : "";
        var staticText = method.IsStatic ? " static" : "";
        var parameters = string.Join(", ", method.GetParameters().Select(p => $"{CleanName(p.ParameterType)} {p.Name}"));
        return $"{method.ReturnType.Name}{staticText}{generic} {method.Name}({parameters})";
    }

    private static string CleanName(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.FullName ?? type.Name;
        }

        var name = type.Name.Split('`')[0];
        return $"{type.Namespace}.{name}<{string.Join(", ", type.GetGenericArguments().Select(CleanName))}>";
    }
}

