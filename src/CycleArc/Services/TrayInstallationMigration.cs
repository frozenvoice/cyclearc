using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json;
using Microsoft.Win32;

namespace CycleArc.Services;

/// <summary>
/// Removes path-specific Windows 11 notification-area records left by verified
/// CycleArc executables after the desktop has moved to its canonical path.
/// Opaque legacy tray caches are deliberately not modified.
/// </summary>
public static class TrayInstallationMigration
{
    private const string NotifyIconSettingsPath = @"Control Panel\NotifyIconSettings";
    private const string ExecutablePathValue = "ExecutablePath";
    private const string IsPromotedValue = "IsPromoted";
    private const int MaximumRows = 512;

    /// <returns>
    /// <see langword="true"/> when exactly one canonical tray row was available,
    /// including when there was nothing eligible to remove. A false result can be
    /// retried after the canonical notification icon has registered itself.
    /// </returns>
    public static bool Run(
        string canonicalExecutable,
        IReadOnlyCollection<string> verifiedOldPaths,
        Action<string>? warn = null)
    {
        if (!TryNormalizeExecutable(canonicalExecutable, out var canonicalPath))
        {
            Warn(warn, "Tray migration skipped: canonical executable path is invalid");
            return false;
        }

        var canonicalDirectory = Path.GetDirectoryName(canonicalPath)!;
        if (!TryValidateBackupDirectory(canonicalDirectory))
        {
            Warn(warn, "Tray migration skipped: canonical directory is unavailable or redirected");
            return false;
        }

        var allowedOldPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var suppliedPath in verifiedOldPaths ?? Array.Empty<string>())
        {
            if (TryNormalizeExecutable(suppliedPath, out var normalized)
                && !normalized.Equals(canonicalPath, StringComparison.OrdinalIgnoreCase))
            {
                allowedOldPaths.Add(normalized);
            }
        }

        try
        {
            using var parent = Registry.CurrentUser.OpenSubKey(NotifyIconSettingsPath, writable: true);
            if (parent is null) return false;

            var names = parent.GetSubKeyNames();
            if (names.Length > MaximumRows)
            {
                Warn(warn, $"Tray migration skipped: notification-area row limit exceeded ({names.Length})");
                return false;
            }

            var rows = ReadRows(parent, names, warn);
            var canonicalRows = rows.Where(row =>
                row.ExecutablePath.Equals(canonicalPath, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (canonicalRows.Length != 1)
            {
                if (canonicalRows.Length > 1)
                    Warn(warn, "Tray migration skipped: canonical notification-area row is ambiguous");
                return false;
            }

            var canonicalRow = canonicalRows[0];
            foreach (var oldRow in rows.Where(row =>
                         allowedOldPaths.Contains(row.ExecutablePath)
                         && IsVerifiedOldCycleArcExecutable(row.ExecutablePath)))
            {
                try
                {
                    BackupRows(canonicalDirectory, canonicalRow, oldRow);
                    if (TryGetPromotedValue(oldRow.Values, out var promotedValue))
                    {
                        using var canonicalKey = parent.OpenSubKey(canonicalRow.Name, writable: true)
                            ?? throw new IOException("Canonical notification-area row disappeared");
                        canonicalKey.SetValue(IsPromotedValue, promotedValue.Value, promotedValue.Kind);
                        canonicalRow = ReadRow(canonicalKey, canonicalRow.Name) ?? canonicalRow;
                    }

                    parent.DeleteSubKeyTree(oldRow.Name, throwOnMissingSubKey: false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                               or ArgumentException or SecurityException)
                {
                    Warn(warn, "Tray migration left an old notification-area row: " + ex.GetType().Name);
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or SecurityException)
        {
            Warn(warn, "Tray migration unavailable: " + ex.GetType().Name);
            return false;
        }
    }

    private static List<TrayRegistryRow> ReadRows(RegistryKey parent, IEnumerable<string> names, Action<string>? warn)
    {
        var rows = new List<TrayRegistryRow>();
        foreach (var name in names)
        {
            try
            {
                using var key = parent.OpenSubKey(name, writable: false);
                if (key is null) continue;
                var row = ReadRow(key, name);
                if (row is not null) rows.Add(row);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or ArgumentException or SecurityException)
            {
                Warn(warn, "Tray migration could not inspect a notification-area row: " + ex.GetType().Name);
            }
        }

        return rows;
    }

    private static TrayRegistryRow? ReadRow(RegistryKey key, string name)
    {
        if (key.GetValue(ExecutablePathValue, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string path
            || !TryNormalizeExecutable(path, out var executablePath))
        {
            return null;
        }

        var values = new List<TrayRegistryValue>();
        foreach (var valueName in key.GetValueNames())
        {
            var kind = key.GetValueKind(valueName);
            var value = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value is null) continue;
            values.Add(TrayRegistryValue.Create(valueName, kind, value));
        }

        return new TrayRegistryRow(name, executablePath, values);
    }

    private static bool IsVerifiedOldCycleArcExecutable(string path)
    {
        try
        {
            if (!File.Exists(path)
                || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
                || !Path.GetFileName(path).Equals("CycleArc.exe", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var version = FileVersionInfo.GetVersionInfo(path);
            return string.Equals(version.ProductName, "CycleArc", StringComparison.OrdinalIgnoreCase)
                && string.Equals(version.OriginalFilename, "CycleArc.dll", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryGetPromotedValue(
        IReadOnlyCollection<TrayRegistryValue> values,
        out TrayRegistryValue promoted)
    {
        promoted = values.FirstOrDefault(value =>
            value.Name.Equals(IsPromotedValue, StringComparison.OrdinalIgnoreCase)
            && value.Kind == RegistryValueKind.DWord
            && value.Value is int number
            && number != 0)!;
        return promoted is not null;
    }

    private static void BackupRows(string directory, TrayRegistryRow canonical, TrayRegistryRow removed)
    {
        var backup = new TrayMigrationBackup(
            SchemaVersion: 1,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            RegistryRoot: @"HKEY_CURRENT_USER\" + NotifyIconSettingsPath,
            CanonicalRow: canonical,
            RemovedRow: removed);
        var finalPath = Path.Combine(directory, $"tray-backup-{Guid.NewGuid():N}.json");
        var temporaryPath = finalPath + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(backup, new JsonSerializerOptions { WriteIndented = true });

        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, finalPath);
        }
        catch
        {
            try { File.Delete(temporaryPath); } catch { }
            throw;
        }
    }

    private static bool TryNormalizeExecutable(string? path, out string normalized)
    {
        normalized = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
            normalized = Path.GetFullPath(path);
            return Path.GetFileName(normalized).Equals("CycleArc.exe", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryValidateBackupDirectory(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return false;
            return (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static void Warn(Action<string>? warn, string message)
    {
        try { warn?.Invoke(message); }
        catch { }
    }

    private sealed record TrayMigrationBackup(
        int SchemaVersion,
        DateTimeOffset CreatedAtUtc,
        string RegistryRoot,
        TrayRegistryRow CanonicalRow,
        TrayRegistryRow RemovedRow);

    private sealed record TrayRegistryRow(
        string Name,
        string ExecutablePath,
        IReadOnlyCollection<TrayRegistryValue> Values);

    private sealed record TrayRegistryValue(string Name, RegistryValueKind Kind, object Value)
    {
        public static TrayRegistryValue Create(string name, RegistryValueKind kind, object value) =>
            value switch
            {
                byte[] bytes => new(name, kind, Convert.ToBase64String(bytes)),
                string[] strings => new(name, kind, strings),
                string text => new(name, kind, text),
                int number => new(name, kind, number),
                long number => new(name, kind, number),
                _ => new(name, kind, value.ToString() ?? string.Empty)
            };
    }
}
