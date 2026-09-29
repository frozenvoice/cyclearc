using System.Reflection;

namespace CycleArc.UI;

/// <summary>The running version as people read it: the release number, with the build identifier kept for tooltips.</summary>
internal static class AppVersionDisplay
{
    public static string Full(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? assembly.GetName().Version?.ToString(3) ?? "?";

    public static string Short(Assembly assembly) => Full(assembly).Split('+')[0];
}
