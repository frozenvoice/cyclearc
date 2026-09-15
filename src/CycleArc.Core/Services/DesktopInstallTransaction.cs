using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CycleArc.Services;

/// <summary>
/// The result of a desktop executable replacement. Failure always describes the
/// original install/start failure, even when the old executable was restored.
/// </summary>
public sealed record DesktopInstallResult(
    bool Succeeded,
    bool RolledBack,
    Exception? Failure,
    string SourceHash,
    string TargetPath,
    string? BackupPath);

/// <summary>
/// Replaces the per-user desktop executable as one file transaction.
/// This class deliberately knows nothing about process, mutex, tray, or account
/// state. Those concerns are supplied by the desktop host callbacks.
/// </summary>
public sealed class DesktopInstallTransaction
{
    public const string TargetFileName = "CycleArc.exe";
    public const string BackupFileName = "CycleArc.exe.previous";
    public const string BackupFilePrefix = BackupFileName + "-";
    public const string LockFileName = ".install.lock";
    public const string JournalFileName = ".install.journal";
    public const string StagingFilePrefix = ".staging-";

    private const int CopyBufferSize = 64 * 1024;
    private const int DefaultRetryAttempts = 30;
    private static readonly TimeSpan DefaultLeaseTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly JsonSerializerOptions JournalJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _canonicalDirectory;
    private readonly string _targetPath;
    private readonly string _lockPath;
    private readonly string _journalPath;
    private readonly string _journalTempPath;
    private readonly Action _stop;
    private readonly Action<string, bool, string> _startAndVerify;
    private readonly Action _stopFailed;
    private readonly TimeSpan _leaseTimeout;
    private readonly int _retryAttempts;
    private readonly TimeSpan _retryDelay;

    public DesktopInstallTransaction(
        string canonicalDirectory,
        Action stop,
        Action<string, bool, string> startAndVerify,
        Action stopFailed,
        TimeSpan? leaseTimeout = null,
        int retryAttempts = DefaultRetryAttempts,
        TimeSpan? retryDelay = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalDirectory);
        ArgumentNullException.ThrowIfNull(stop);
        ArgumentNullException.ThrowIfNull(startAndVerify);
        ArgumentNullException.ThrowIfNull(stopFailed);

        _canonicalDirectory = Path.GetFullPath(canonicalDirectory);
        _targetPath = Path.Combine(_canonicalDirectory, TargetFileName);
        _lockPath = Path.Combine(_canonicalDirectory, LockFileName);
        _journalPath = Path.Combine(_canonicalDirectory, JournalFileName);
        _journalTempPath = _journalPath + ".tmp";
        _stop = stop;
        _startAndVerify = startAndVerify;
        _stopFailed = stopFailed;
        _leaseTimeout = leaseTimeout ?? DefaultLeaseTimeout;
        _retryAttempts = retryAttempts;
        _retryDelay = retryDelay ?? DefaultRetryDelay;

        if (_leaseTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseTimeout));
        if (retryAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(retryAttempts));
        if (_retryDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retryDelay));
    }

    public string CanonicalDirectory => _canonicalDirectory;

    public string TargetPath => _targetPath;

    // Stable base name for callers that need to display the sibling convention.
    // Each transaction uses a unique generated suffix.
    public string BackupPath => Path.Combine(_canonicalDirectory, BackupFileName);

    public DesktopInstallResult Install(string sourcePath, string expectedSha256)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(expectedSha256);

        var source = Path.GetFullPath(sourcePath);
        if (PathsEqual(source, _targetPath))
            throw new ArgumentException("The source executable must be different from the installed executable.", nameof(sourcePath));
        var expectedHash = NormalizeHash(expectedSha256);
        var transactionId = Guid.NewGuid().ToString("N");
        var stagingPath = Path.Combine(_canonicalDirectory, StagingFilePrefix + transactionId + ".exe");
        var backupPath = Path.Combine(_canonicalDirectory, BackupFilePrefix + transactionId + ".exe");

        var sourceHash = string.Empty;
        var targetExisted = false;
        var stopped = false;
        var replacementApplied = false;
        var startAttempted = false;
        var ownJournal = false;
        Exception? failure = null;
        var rolledBack = false;
        FileStream? lease = null;

        try
        {
            EnsureCanonicalDirectory();
            ValidatePathNoReparse(source);
            ValidatePathNoReparse(_targetPath);
            ValidatePathNoReparse(stagingPath);
            ValidatePathNoReparse(backupPath);
            ValidateSourceFile(source);

            lease = AcquireLease();

            // Copy and hash while the source is open. The expected digest is
            // checked before stop() can run.
            sourceHash = CopyAndHash(source, stagingPath);
            if (!string.Equals(sourceHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"The source executable hash does not match the expected SHA-256 ({sourceHash}).");

            ValidateStagedFile(stagingPath, expectedHash);
            // A previous interrupted swap is recovered only after this
            // candidate has passed its own hash check. A bad download must not
            // stop a running desktop merely because a journal is present.
            RecoverPendingTransaction();
            ValidateDestinationBeforeStop();
            targetExisted = File.Exists(_targetPath);
            WriteJournal(new InstallJournal(
                _targetPath, backupPath, stagingPath, targetExisted, JournalPhase.Prepared));
            ownJournal = true;
            // Make the stop boundary recoverable: if the installer dies while
            // the host is stopping, the next invocation can finish the stop
            // and restart the verified previous executable.
            WriteJournal(new InstallJournal(
                _targetPath, backupPath, stagingPath, targetExisted, JournalPhase.Stopping));

            _stop();
            stopped = true;

            // Ensure the destination did not change while the host stopped the
            // desktop process. The callback owns the process/mutex boundary.
            ValidateCanonicalDirectory();
            ValidatePathNoReparse(_targetPath);
            ValidatePathNoReparse(backupPath);
            var targetStillExists = File.Exists(_targetPath);
            if (Directory.Exists(_targetPath))
                throw new IOException("The executable destination is a directory.");
            if (targetStillExists != targetExisted)
                throw new IOException("The executable destination changed while the desktop was stopping.");
            WriteJournal(new InstallJournal(
                _targetPath, backupPath, stagingPath, targetExisted, JournalPhase.Stopped));

            if (targetExisted)
                ReplaceFileWithRetry(stagingPath, _targetPath, backupPath);
            else
                MoveFileWithRetry(stagingPath, _targetPath);
            replacementApplied = true;
            WriteJournal(new InstallJournal(
                _targetPath, backupPath, stagingPath, targetExisted, JournalPhase.Swapped));

            startAttempted = true;
            _startAndVerify(_targetPath, false, sourceHash);
            WriteJournal(new InstallJournal(
                _targetPath, backupPath, stagingPath, targetExisted, JournalPhase.Committed));
            TryDeleteFile(_journalPath);
            CleanupOldBackups(backupPath);

            return new DesktopInstallResult(
                Succeeded: true,
                RolledBack: false,
                Failure: null,
                SourceHash: sourceHash,
                TargetPath: _targetPath,
                BackupPath: targetExisted ? backupPath : null);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            if (failure is not null && stopped)
            {
                rolledBack = TryRollback(
                    targetExisted,
                    replacementApplied,
                    startAttempted,
                    backupPath,
                    stagingPath);
            }
            else if (failure is not null)
            {
                // Prepared is safe to discard because no stop/swap completed.
                if (ownJournal) TryDeleteFile(_journalPath);
            }

            TryDeleteFile(stagingPath);
            lease?.Dispose();
        }

        return new DesktopInstallResult(
            Succeeded: false,
            RolledBack: rolledBack,
            Failure: failure,
            SourceHash: sourceHash,
            TargetPath: _targetPath,
            BackupPath: targetExisted ? backupPath : null);
    }

    private bool TryRollback(
        bool targetExisted,
        bool replacementApplied,
        bool startAttempted,
        string backupPath,
        string stagingPath)
    {
        // Do not touch either executable while a failed process may still hold
        // the new file or desktop mutex. A pending journal enables a later,
        // bounded recovery attempt.
        if (startAttempted)
        {
            try { _stopFailed(); }
            catch { return false; }
        }

        string? previousHash = null;
        try
        {
            ValidateCanonicalDirectory();
            ValidatePathNoReparse(_targetPath);
            ValidatePathNoReparse(backupPath);
            ValidatePathNoReparse(stagingPath);

            if (replacementApplied)
            {
                if (targetExisted)
                {
                    if (!File.Exists(backupPath))
                        throw new FileNotFoundException("The previous executable backup is missing.", backupPath);
                    previousHash = ComputeHash(backupPath);
                    TryDeleteFile(stagingPath);
                    ReplaceFileWithRetry(backupPath, _targetPath, stagingPath);
                }
                else
                {
                    DeleteFileWithRetry(_targetPath);
                }
            }
            else if (targetExisted)
            {
                // stop() succeeded but the swap did not. Restart the still
                // current executable before reporting the failure.
                previousHash = ComputeHash(_targetPath);
            }

            WriteJournal(new InstallJournal(
                _targetPath, backupPath, stagingPath, targetExisted, JournalPhase.Restored));
        }
        catch
        {
            return false;
        }

        if (targetExisted)
        {
            try { _startAndVerify(_targetPath, true, previousHash!); }
            catch
            {
                // startAndVerify may have launched a process before verifying
                // it. Give the host one chance to stop that exact process.
                try { _stopFailed(); }
                catch { }
                return false;
            }
        }

        TryDeleteFile(_journalPath);
        CleanupOldBackups(null);
        return true;
    }

    private void RecoverPendingTransaction()
    {
        if (!File.Exists(_journalPath))
        {
            ValidatePathNoReparse(_journalTempPath);
            TryDeleteFile(_journalTempPath);
            return;
        }

        var journal = ReadJournal();
        switch (journal.Phase)
        {
            case JournalPhase.Prepared:
                TryDeleteFile(journal.StagingPath);
                DeleteJournalWithRetry();
                return;

            case JournalPhase.Stopping:
                _stop();
                if (journal.TargetExisted)
                {
                    if (!File.Exists(_targetPath))
                        throw new IOException("A stopping transaction lost its previous executable.");
                    StartPreviousDuringRecovery(ComputeHash(_targetPath));
                }
                TryDeleteFile(journal.StagingPath);
                DeleteJournalWithRetry();
                return;

            case JournalPhase.Stopped:
                // No swap was recorded. The old target is still in place, but
                // the previous desktop may have been stopped before a crash.
                if (SwapAppearsApplied(journal))
                {
                    _stop();
                    RecoverSwappedTransaction(journal);
                    return;
                }
                if (journal.TargetExisted)
                {
                    if (!File.Exists(_targetPath))
                        throw new IOException("A stopped transaction lost its previous executable.");
                    StartPreviousDuringRecovery(ComputeHash(_targetPath));
                }
                TryDeleteFile(journal.StagingPath);
                DeleteJournalWithRetry();
                return;

            case JournalPhase.Swapped:
                // The new executable was written but has not been committed as
                // ready. Stop any process that may have started after a crash,
                // restore the old bytes atomically, then verify the old app.
                _stop();
                RecoverSwappedTransaction(journal);
                return;

            case JournalPhase.Restored:
                if (journal.TargetExisted)
                {
                    if (!File.Exists(_targetPath))
                        throw new IOException("A restored transaction is missing its previous executable.");
                    StartPreviousDuringRecovery(ComputeHash(_targetPath));
                }
                DeleteJournalWithRetry();
                TryDeleteFile(journal.StagingPath);
                return;

            case JournalPhase.Committed:
                TryDeleteFile(journal.StagingPath);
                DeleteJournalWithRetry();
                CleanupOldBackups(null);
                return;

            default:
                throw new InvalidDataException("The desktop install journal has an unknown phase.");
        }
    }

    private void RecoverSwappedTransaction(InstallJournal journal)
    {
        var previousHash = string.Empty;
        if (journal.TargetExisted)
        {
            if (!File.Exists(journal.BackupPath))
                throw new FileNotFoundException("The pending transaction backup is missing.", journal.BackupPath);
            previousHash = ComputeHash(journal.BackupPath);
            TryDeleteFile(journal.StagingPath);
            ReplaceFileWithRetry(journal.BackupPath, _targetPath, journal.StagingPath);
        }
        else
        {
            DeleteFileWithRetry(_targetPath);
        }

        WriteJournal(journal with { Phase = JournalPhase.Restored });
        if (journal.TargetExisted)
        {
            try { _startAndVerify(_targetPath, true, previousHash); }
            catch
            {
                try { _stopFailed(); }
                catch { }
                throw;
            }
        }

        DeleteJournalWithRetry();
        TryDeleteFile(journal.StagingPath);
        CleanupOldBackups(null);
    }

    private void StartPreviousDuringRecovery(string oldHash)
    {
        try { _startAndVerify(_targetPath, true, oldHash); }
        catch
        {
            try { _stopFailed(); }
            catch { }
            throw;
        }
    }

    private void ValidateCanonicalDirectory()
    {
        ValidatePathNoReparse(_canonicalDirectory);
        if (!Directory.Exists(_canonicalDirectory))
            throw new DirectoryNotFoundException($"The install directory does not exist: {_canonicalDirectory}");
        if (File.Exists(_canonicalDirectory))
            throw new IOException("The install directory is a file.");
    }

    private void EnsureCanonicalDirectory()
    {
        ValidatePathNoReparse(_canonicalDirectory);
        if (File.Exists(_canonicalDirectory))
            throw new IOException("The install directory is a file.");
        if (!Directory.Exists(_canonicalDirectory))
            Directory.CreateDirectory(_canonicalDirectory);
        ValidateCanonicalDirectory();
    }

    private bool SwapAppearsApplied(InstallJournal journal)
    {
        if (!File.Exists(_targetPath)) return false;
        if (journal.TargetExisted)
            return File.Exists(journal.BackupPath) && !File.Exists(journal.StagingPath);
        return !File.Exists(journal.StagingPath);
    }

    private void ValidateDestinationBeforeStop()
    {
        ValidatePathNoReparse(_targetPath);
        if (Directory.Exists(_targetPath))
            throw new IOException("The executable destination is a directory.");
    }

    private static void ValidateSourceFile(string sourcePath)
    {
        if (Directory.Exists(sourcePath))
            throw new InvalidDataException("The source executable path is a directory.");
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The source executable does not exist.", sourcePath);

        var attributes = File.GetAttributes(sourcePath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Reparse-point source files are not allowed: {sourcePath}");
    }

    private void ValidateStagedFile(string stagingPath, string expectedHash)
    {
        ValidatePathNoReparse(stagingPath);
        if (!File.Exists(stagingPath))
            throw new FileNotFoundException("The staged executable disappeared before installation.", stagingPath);
        var stagedHash = ComputeHash(stagingPath);
        if (!string.Equals(stagedHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The staged executable hash changed before installation.");
    }

    private FileStream AcquireLease()
    {
        var stopwatch = Stopwatch.StartNew();
        Exception? lastFailure = null;
        while (true)
        {
            // Safety/input failures are permanent. Only a sharing violation
            // should consume the bounded lease timeout.
            ValidatePathNoReparse(_lockPath);
            if (Directory.Exists(_lockPath))
                throw new IOException("The install lock path is a directory.");

            try
            {
                return new FileStream(
                    _lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    options: FileOptions.WriteThrough);
            }
            catch (IOException ex) { lastFailure = ex; }
            catch (UnauthorizedAccessException ex) { lastFailure = ex; }

            if (stopwatch.Elapsed >= _leaseTimeout)
                throw new TimeoutException("Timed out acquiring the desktop install lock.", lastFailure);
            Thread.Sleep(GetDelay(stopwatch));
        }
    }

    private string CopyAndHash(string sourcePath, string stagingPath)
    {
        using var source = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.SequentialScan);
        using var staged = new FileStream(
            stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize,
            FileOptions.SequentialScan | FileOptions.WriteThrough);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[CopyBufferSize];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            staged.Write(buffer, 0, read);
            hash.AppendData(buffer, 0, read);
        }
        staged.Flush(flushToDisk: true);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string ComputeHash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private void WriteJournal(InstallJournal journal)
    {
        ValidateJournal(journal);
        ValidatePathNoReparse(_journalTempPath);
        TryDeleteFile(_journalTempPath);
        using (var stream = new FileStream(
                   _journalTempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                   FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, journal, JournalJsonOptions);
            stream.Flush(flushToDisk: true);
        }

        RetryIo(
            () =>
            {
                if (File.Exists(_journalPath))
                    File.Replace(_journalTempPath, _journalPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                else
                    File.Move(_journalTempPath, _journalPath);
            },
            "commit install journal");
    }

    private InstallJournal ReadJournal()
    {
        if (Directory.Exists(_journalPath))
            throw new IOException("The install journal path is a directory.");
        ValidatePathNoReparse(_journalPath);
        try
        {
            using var stream = new FileStream(
                _journalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !HasJournalProperty(document.RootElement, nameof(InstallJournal.TargetPath), JsonValueKind.String)
                || !HasJournalProperty(document.RootElement, nameof(InstallJournal.BackupPath), JsonValueKind.String)
                || !HasJournalProperty(document.RootElement, nameof(InstallJournal.StagingPath), JsonValueKind.String)
                || !HasJournalProperty(document.RootElement, nameof(InstallJournal.TargetExisted), JsonValueKind.True, JsonValueKind.False)
                || !HasJournalProperty(document.RootElement, nameof(InstallJournal.Phase), JsonValueKind.String))
                throw new InvalidDataException("The desktop install journal is missing required fields.");
            var journal = JsonSerializer.Deserialize<InstallJournal>(document.RootElement.GetRawText(), JournalJsonOptions)
                ?? throw new InvalidDataException("The desktop install journal is empty.");
            ValidateJournal(journal);
            return journal;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The desktop install journal is invalid.", ex);
        }
    }

    private static bool HasJournalProperty(JsonElement element, string name, params JsonValueKind[] kinds)
        => element.TryGetProperty(name, out var value) && kinds.Contains(value.ValueKind);

    private void ValidateJournal(InstallJournal journal)
    {
        if (!PathsEqual(journal.TargetPath, _targetPath))
            throw new InvalidDataException("The desktop install journal target is outside the canonical directory.");
        if (!IsGeneratedSibling(journal.BackupPath, BackupFilePrefix, ".exe")
            || !IsGeneratedSibling(journal.StagingPath, StagingFilePrefix, ".exe"))
            throw new InvalidDataException("The desktop install journal contains an unsafe sibling path.");

        var canonical = Path.GetFullPath(_canonicalDirectory);
        if (!PathsEqual(Path.GetDirectoryName(Path.GetFullPath(journal.BackupPath))!, canonical)
            || !PathsEqual(Path.GetDirectoryName(Path.GetFullPath(journal.StagingPath))!, canonical))
            throw new InvalidDataException("The desktop install journal contains an out-of-directory path.");
        ValidatePathNoReparse(journal.TargetPath);
        ValidatePathNoReparse(journal.BackupPath);
        ValidatePathNoReparse(journal.StagingPath);
    }

    private void DeleteJournalWithRetry() => DeleteFileWithRetry(_journalPath);

    private void CleanupOldBackups(string? activeBackupPath)
    {
        try
        {
            var backups = Directory.EnumerateFiles(_canonicalDirectory, BackupFilePrefix + "*.exe")
                .Where(path => IsGeneratedSibling(path, BackupFilePrefix, ".exe"))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();
            foreach (var path in backups) ValidatePathNoReparse(path);
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (activeBackupPath is not null)
                keep.Add(Path.GetFullPath(activeBackupPath));
            foreach (var path in backups)
            {
                if (keep.Count >= 2) break;
                keep.Add(Path.GetFullPath(path));
            }
            foreach (var path in backups)
            {
                if (keep.Contains(Path.GetFullPath(path))) continue;
                TryDeleteFile(path);
            }
        }
        catch { }
    }

    private void ReplaceFileWithRetry(string sourcePath, string destinationPath, string backupPath)
        => RetryIo(() => File.Replace(sourcePath, destinationPath, backupPath, ignoreMetadataErrors: true), "replace executable");

    private void MoveFileWithRetry(string sourcePath, string destinationPath)
        => RetryIo(() => File.Move(sourcePath, destinationPath), "move executable");

    private void DeleteFileWithRetry(string path)
    {
        RetryIo(
            () =>
            {
                if (Directory.Exists(path))
                    throw new IOException($"Cannot delete directory as a file: {path}");
                if (File.Exists(path)) File.Delete(path);
            },
            "delete executable file");
    }

    private void RetryIo(Action action, string operation)
    {
        Exception? lastFailure = null;
        for (var attempt = 0; attempt < _retryAttempts; attempt++)
        {
            try { action(); return; }
            catch (IOException ex) { lastFailure = ex; }
            catch (UnauthorizedAccessException ex) { lastFailure = ex; }
            if (attempt + 1 < _retryAttempts) Thread.Sleep(_retryDelay);
        }
        throw new IOException($"Unable to {operation} after {_retryAttempts} attempts.", lastFailure);
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) DeleteFileWithRetry(path);
        }
        catch { }
    }

    private TimeSpan GetDelay(Stopwatch stopwatch)
    {
        var remaining = _leaseTimeout - stopwatch.Elapsed;
        return remaining <= TimeSpan.Zero
            ? TimeSpan.Zero
            : remaining < _retryDelay ? remaining : _retryDelay;
    }

    private static string NormalizeHash(string value)
    {
        if (value.Length != 64)
            throw new ArgumentException("The expected SHA-256 must contain 64 hexadecimal characters.", nameof(value));
        try { _ = Convert.FromHexString(value); }
        catch (FormatException ex)
        {
            throw new ArgumentException("The expected SHA-256 is not hexadecimal.", nameof(value), ex);
        }
        return value.ToUpperInvariant();
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static bool IsGeneratedSibling(string path, string prefix, string suffix)
    {
        var fileName = Path.GetFileName(Path.GetFullPath(path));
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal)
            || !fileName.EndsWith(suffix, StringComparison.Ordinal)) return false;
        var id = fileName[prefix.Length..^suffix.Length];
        return id.Length == 32 && id.All(IsLowerHex);
    }

    private static bool IsLowerHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static void ValidatePathNoReparse(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
            ValidateAttributes(fullPath);
        for (var directory = Directory.GetParent(fullPath);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(directory.FullName) || Directory.Exists(directory.FullName))
                ValidateAttributes(directory.FullName);
        }
    }

    private static void ValidateAttributes(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Reparse-point paths are not allowed: {path}");
    }

    private enum JournalPhase
    {
        Prepared,
        Stopping,
        Stopped,
        Swapped,
        Restored,
        Committed
    }

    private sealed record InstallJournal(
        string TargetPath,
        string BackupPath,
        string StagingPath,
        bool TargetExisted,
        JournalPhase Phase);
}
