namespace CycleArc.Updates;

/// <summary>A stable application release advertised by the update source.</summary>
public sealed record AppUpdateRelease(string Version, string Notes);

/// <summary>The operations a concrete update client must provide to the coordinator.</summary>
public interface IAppUpdateClient
{
    /// <summary>Gets whether this process is running from a managed installation.</summary>
    bool IsInstalled { get; }

    /// <summary>Gets the version of the currently running installed build.</summary>
    string CurrentVersion { get; }

    /// <summary>Checks the configured release source. A null result means no release is available.</summary>
    Task<AppUpdateRelease?> CheckAsync(CancellationToken token);

    /// <summary>Downloads and validates a complete release package.</summary>
    Task DownloadAsync(AppUpdateRelease release, IProgress<int> progress, CancellationToken token);

    /// <summary>Stages the downloaded release for application after the current process exits.</summary>
    void ApplyOnExit(AppUpdateRelease release);
}

/// <summary>Coarse, safe-to-display update failure categories.</summary>
public enum AppUpdateError
{
    CheckFailed,
    DownloadFailed,
    Canceled,
    InvalidCurrentVersion,
    ApplyFailed,
}

/// <summary>Lifecycle state exposed to the desktop UI.</summary>
public enum AppUpdateState
{
    Disabled,
    Idle,
    Checking,
    Available,
    Downloading,
    Ready,
    Applying,
    Failed,
}
