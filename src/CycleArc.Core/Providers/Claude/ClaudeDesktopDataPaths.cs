using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("CycleArc.Tests")]

namespace CycleArc.Providers.Claude;

internal static class ClaudeDesktopDataPaths
{
    internal static IEnumerable<string> KnownRoots() => KnownRoots(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    internal static IEnumerable<string> KnownRoots(string? localAppData, string? appData)
    {
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            // Newer Desktop builds migrate out of the MSIX virtualized folder.
            // Keep discovery to exact known roots, never a recursive profile scan.
            yield return Path.Combine(localAppData, "Claude-Data", "claude_pzs8sxrjxfjjc");
            yield return Path.Combine(localAppData, "Packages", "Claude_pzs8sxrjxfjjc", "LocalCache", "Roaming", "Claude");
        }

        if (!string.IsNullOrWhiteSpace(appData)) yield return Path.Combine(appData, "Claude");
    }
}
