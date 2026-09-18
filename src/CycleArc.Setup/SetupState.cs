namespace CycleArc.Setup;

/// <summary>
/// Reports what the window is waiting on, for a parent process that launched this installer.
/// A build script cannot otherwise tell "the person is reading the confirmation screen" from
/// "the installation is running", and must not cut the first one short.
/// Enabled only when CYCLEARC_SETUP_STATE_FILE is set; a person double-clicking sees nothing.
/// </summary>
internal static class SetupState
{
    public const string Variable = "CYCLEARC_SETUP_STATE_FILE";

    public const string AwaitingApproval = "awaiting-approval";
    public const string Installing = "installing";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    private static readonly string? Path = Environment.GetEnvironmentVariable(Variable);

    public static void Report(string state)
    {
        if (string.IsNullOrWhiteSpace(Path)) return;
        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(Path, state + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Never let progress reporting take down an installation.
        }
    }
}
