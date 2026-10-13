namespace CycleArc.Services;

public sealed class SettingsStore
{
    private readonly string _path;
    // Selection of the recovery slot and the commit must also be serialized across
    // separate store instances. Desktop ownership already excludes other app processes.
    private static readonly object Gate = new();
    public bool RecoveredFromBackup { get; private set; }
    public string BackupPath => _path + ".bak";
    public string RecoveryBackupPath => BackupPath + ".next";
    public bool BackupRotationDeferred { get; private set; }

    public SettingsStore(string? path = null)
    {
        _path = path ?? AppPaths.Settings;
    }

    public AppSettings Load()
    {
        lock (Gate)
        {
            RecoveredFromBackup = false;
            BackupRotationDeferred = File.Exists(RecoveryBackupPath);
            if (TryRead(_path, out var settings)) return settings!;
            // A deferred rotation contains the immediate previous primary; .bak
            // may still be an older version held open by another reader.
            if (TryRead(RecoveryBackupPath, out settings) || TryRead(BackupPath, out settings))
            {
                RecoveredFromBackup = true;
                return settings!;
            }
            return File.Exists(_path) || File.Exists(RecoveryBackupPath) || File.Exists(BackupPath)
                ? AppSettings.CreateDefaults() : AppSettings.CreateNewInstall();
        }
    }

    public void Save(AppSettings settings)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    JsonSerializer.Serialize(stream, settings, new JsonSerializerOptions { WriteIndented = true });
                    stream.Flush(flushToDisk: true);
                }
                if (TryRead(_path, out _))
                    ReplacePrimary(temporary);
                else
                {
                    // An unreadable/corrupt primary must never replace a known-good backup.
                    File.Move(temporary, _path, overwrite: true);
                    BackupRotationDeferred = File.Exists(RecoveryBackupPath);
                }
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private void ReplacePrimary(string temporary)
    {
        var backup = File.Exists(RecoveryBackupPath) ? RecoveryBackupPath : BackupPath;
        try
        {
            File.Replace(temporary, _path, backup, ignoreMetadataErrors: true);
        }
        catch (IOException ex) when (ex.HResult == unchecked((int)0x80070497)
            && backup == BackupPath && !File.Exists(RecoveryBackupPath) && File.Exists(temporary))
        {
            // Windows 1175 retains the primary and replacement names. A locked
            // existing .bak can cause it; use a fresh, bounded recovery destination.
            // Do not repeat the blocked operation or catch primary/other I/O failures.
            File.Replace(temporary, _path, RecoveryBackupPath, ignoreMetadataErrors: true);
            backup = RecoveryBackupPath;
        }

        if (backup == RecoveryBackupPath)
        {
            try
            {
                // Normalize only AFTER the primary commit: a failed primary replacement
                // must not have advanced either backup. This rename also survives restart.
                File.Move(RecoveryBackupPath, BackupPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The save succeeded; .next still holds the previous valid settings.
                // Backup maintenance failure must not be reported as a failed save.
                BackupRotationDeferred = true;
                return;
            }
        }
        BackupRotationDeferred = false;
    }

    private static bool TryRead(string path, out AppSettings? settings)
    {
        settings = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            settings = SettingsMigration.FromJson(json);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return false;
        }
    }
}
