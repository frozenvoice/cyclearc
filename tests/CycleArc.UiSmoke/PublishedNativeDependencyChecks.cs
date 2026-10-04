using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using CycleArc.Providers.Cursor;
using CycleArc.Services;
using Microsoft.Data.Sqlite;

namespace CycleArc.UiSmoke;

internal static class PublishedNativeDependencyChecks
{
    public static void Run(string executable)
    {
        executable = Path.GetFullPath(executable);
        Check(File.Exists(executable), "The published test-flavour executable is missing.");
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        var root = Path.GetFullPath(Path.Combine(temporaryRoot,
            "cyclearc-native-probe-" + Guid.NewGuid().ToString("N")));
        var database = Path.Combine(root, "synthetic-state.vscdb");
        var extraction = Path.Combine(root, "extracted");
        var workingDirectory = Path.Combine(root, "empty-working-directory");
        Directory.CreateDirectory(workingDirectory);
        using var mutex = new Mutex(false, LegacyInstallation.SingleInstanceMutexName);
        var ownsMutex = false;
        try
        {
            // A routing regression must stop at the existing desktop mutex rather than
            // initialize real settings/accounts. A correctly routed probe ignores it.
            try { ownsMutex = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { ownsMutex = true; }
            SeedDatabase(database);
            var before = Hash(database);
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("--test-native-dependencies");
            start.ArgumentList.Add(database);
            // Force cold extraction instead of reusing an earlier successful bundle.
            start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = extraction;
            using var child = Process.Start(start)
                ?? throw new InvalidOperationException("The published native dependency probe did not start.");
            var stdout = child.StandardOutput.ReadToEndAsync();
            var stderr = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(30_000))
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit(5_000);
                throw new TimeoutException("The published native dependency probe did not finish.");
            }
            Check(child.ExitCode == 0, $"The published native dependency probe failed (exit {child.ExitCode}).");
            Check(Task.WhenAll(stdout, stderr).Wait(TimeSpan.FromSeconds(5)), "The native probe output did not close.");
            Check(stdout.Result.Trim() == "native-dependencies-ok" && stderr.Result.Length == 0,
                "The native dependency probe did not confirm its checks or emitted unexpected diagnostic data.");
            Check(Directory.Exists(extraction)
                && Directory.EnumerateFiles(extraction, "e_sqlite3.dll", SearchOption.AllDirectories).Any(),
                "The single-file bundle did not extract its native SQLite engine.");
            Check(before.SequenceEqual(Hash(database)), "The native dependency probe changed its read-only fixture.");
            Check(!File.Exists(database + "-wal") && !File.Exists(database + "-shm")
                && !File.Exists(database + "-journal"), "The native dependency probe created writable database sidecars.");
            Check(!File.Exists(Path.Combine(root, "settings.json")), "The native dependency probe entered desktop startup.");
            Console.WriteLine("PASS: published single-file native SQLite extraction, engine query and exact Cursor fixture row; read-only, no live account access.");
        }
        finally
        {
            if (ownsMutex) mutex.ReleaseMutex();
            // This routine owns only the newly generated temporary directory.
            if (!root.StartsWith(Path.TrimEndingDirectorySeparator(temporaryRoot) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(root).StartsWith("cyclearc-native-probe-", StringComparison.Ordinal))
                throw new InvalidOperationException("Invalid native probe cleanup target.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void SeedDatabase(string database)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = database, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE ItemTable (key TEXT PRIMARY KEY, value TEXT);"
            + "INSERT INTO ItemTable(key,value) VALUES ($key,$value),($unrelated,$ignored);";
        command.Parameters.AddWithValue("$key", CursorAuthStateDatabaseReader.AccessTokenKey);
        command.Parameters.AddWithValue("$value", "cyclearc-synthetic-native-probe");
        command.Parameters.AddWithValue("$unrelated", "cursorAuth/refreshToken");
        command.Parameters.AddWithValue("$ignored", "synthetic-unrelated-row-must-not-be-selected");
        command.ExecuteNonQuery();
    }

    private static byte[] Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
