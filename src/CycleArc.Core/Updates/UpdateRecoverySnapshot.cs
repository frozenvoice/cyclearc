namespace CycleArc.Updates;

/// <summary>A content-addressed file captured in an update recovery snapshot.</summary>
public sealed record UpdateRecoveryFile(string RelativePath, long Size, string Sha256);

/// <summary>
/// An external copy of an installed Velopack <c>current</c> directory that can restore the
/// previous build after an update starts but fails its readiness check.
/// </summary>
public sealed record UpdateRecoverySnapshot(
    string InstallationRoot,
    string SnapshotDirectory,
    string Version,
    string ExecutableSha256,
    UpdateRecoveryFile[] Files)
{
    private const int MaximumFileCount = 256;
    private const long MaximumTotalSize = 1L * 1024 * 1024 * 1024;
    private const int MaximumTraversalDepth = 32;
    private const int MaximumDirectoryCount = 4096;
    private const string CurrentDirectoryName = "current";
    // Keep boot files in a reserved directory so a current build may legitimately contain a
    // file named Squirrel.exe or CycleArc_ExecutionStub.exe without being dropped on restore.
    private static readonly string UpdateBackupName = Path.Combine(".root", "Squirrel.exe");
    private static readonly string StubBackupName = Path.Combine(".root", "CycleArc_ExecutionStub.exe");

    /// <summary>Copies the current installed file tree to an external recovery directory.</summary>
    public static UpdateRecoverySnapshot Create(
        string installationRoot,
        string snapshotDirectory,
        string version)
    {
        var root = NormalizePath(installationRoot, "installation root");
        var snapshot = NormalizePath(snapshotDirectory, "snapshot directory");
        ValidateVersion(version);
        ValidateRootDirectory(root, "installation root");
        EnsureRootsDoNotOverlap(root, snapshot);

        var current = Path.Combine(root, CurrentDirectoryName);
        ValidateDirectory(current, "current directory");
        EnsureSnapshotDestination(snapshot);

        var sources = EnumerateFilesForRoot(current);
        AddOptionalRootFile(root, "Update.exe", UpdateBackupName, sources);
        AddOptionalRootFile(root, "CycleArc.exe", StubBackupName, sources);
        ValidateSourceInventory(sources);
        sources = HashSources(sources);

        try
        {
            Directory.CreateDirectory(snapshot);
            foreach (var source in sources)
            {
                var destination = Path.Combine(snapshot, source.RelativePath);
                var destinationDirectory = Path.GetDirectoryName(destination)
                    ?? throw new InvalidDataException("The recovery destination path is invalid.");
                Directory.CreateDirectory(destinationDirectory);
                File.Copy(source.SourcePath, destination, overwrite: false);
            }

            var files = sources
                .Select(source => new UpdateRecoveryFile(source.RelativePath, source.Size, source.Sha256!))
                .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var executable = files.FirstOrDefault(file =>
                file.RelativePath.Equals("CycleArc.exe", StringComparison.OrdinalIgnoreCase));
            if (executable is null)
                throw new InvalidDataException("The recovery snapshot is missing CycleArc.exe.");

            var result = new UpdateRecoverySnapshot(root, snapshot, version, executable.Sha256, files);
            result.Verify();
            return result;
        }
        catch
        {
            try
            {
                if (Directory.Exists(snapshot)
                    && (File.GetAttributes(snapshot) & FileAttributes.ReparsePoint) == 0)
                    Directory.Delete(snapshot, recursive: true);
            }
            catch { /* Preserve the original capture failure. */ }
            throw;
        }
    }

    /// <summary>Verifies every recorded file and rejects additions or metadata changes.</summary>
    public void Verify()
    {
        var root = NormalizePath(InstallationRoot, "installation root");
        var snapshot = NormalizePath(SnapshotDirectory, "snapshot directory");
        ValidateVersion(Version);
        ValidateRootDirectory(root, "installation root");
        EnsureRootsDoNotOverlap(root, snapshot);
        ValidateDirectory(snapshot, "snapshot directory");

        if (Files is null || Files.Length is < 2 or > MaximumFileCount)
            throw new InvalidDataException("The recovery file inventory is invalid.");
        if (ExecutableSha256 is null || !IsSha256(ExecutableSha256))
            throw new InvalidDataException("The recovery executable checksum is invalid.");

        var records = new Dictionary<string, UpdateRecoveryFile>(StringComparer.OrdinalIgnoreCase);
        long totalSize = 0;
        foreach (var file in Files)
        {
            if (file is null || !IsSafeRelativePath(file.RelativePath)
                || file.Size < 0 || file.Size > MaximumTotalSize || !IsSha256(file.Sha256))
                throw new InvalidDataException("The recovery file inventory contains an unsafe entry.");
            if (!records.TryAdd(file.RelativePath, file))
                throw new InvalidDataException("The recovery file inventory contains duplicate paths.");
            totalSize = checked(totalSize + file.Size);
            if (totalSize > MaximumTotalSize)
                throw new InvalidDataException("The recovery snapshot is larger than the supported limit.");
        }

        var executable = RequireRecord(records, "CycleArc.exe");
        _ = RequireRecord(records, "sq.version");
        if (!ExecutableSha256.Equals(executable.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The recovery executable checksum is inconsistent.");

        var actualSources = EnumerateFilesForRoot(snapshot);
        ValidateSourceInventoryBounds(actualSources);
        var actualFiles = HashSources(actualSources).ToDictionary(
            file => file.RelativePath,
            file => file,
            StringComparer.OrdinalIgnoreCase);
        if (actualFiles.Count != records.Count || actualFiles.Keys.Any(path => !records.ContainsKey(path)))
            throw new InvalidDataException("The recovery snapshot contains unrecorded files.");

        foreach (var record in records.Values)
        {
            var file = actualFiles[record.RelativePath];
            if (file.Size != record.Size || !file.Sha256!.Equals(record.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The recovery snapshot checksum or size does not match.");
        }

        ValidateSnapshotDirectories(snapshot, records.Keys);
    }

    /// <summary>
    /// Restores <c>current</c> through same-volume directory renames. The external snapshot is
    /// never removed, including when staging or activation fails.
    /// </summary>
    public void Restore() => Restore(null, null);

    /// <summary>
    /// Testable overload that runs after the old current directory is moved aside and before
    /// the staged directory is activated. Production callers should use <see cref="Restore()"/>.
    /// </summary>
    public void Restore(Action? beforeActivate) => Restore(beforeActivate, null);

    // The second hook is internal so focused tests can deterministically fail after the first
    // root-file copy without exposing a production callback surface.
    internal void Restore(Action? beforeActivate, Action? afterFirstRootFile)
    {
        Verify();
        var root = NormalizePath(InstallationRoot, "installation root");
        var snapshot = NormalizePath(SnapshotDirectory, "snapshot directory");
        var current = Path.Combine(root, CurrentDirectoryName);
        var stage = Path.Combine(root, $".recovery-current-{Guid.NewGuid():N}");
        var failed = Path.Combine(root, $".failed-current-{Guid.NewGuid():N}");
        var rootRollback = Path.Combine(root, $".recovery-root-old-{Guid.NewGuid():N}");
        IReadOnlyList<RootFileRollback> rootFiles = [];
        var movedOld = false;
        var activated = false;

        try
        {
            // Capture both root boot files before moving or replacing any installation content.
            rootFiles = CaptureRootFiles(root, rootRollback);
            CopyCurrentToStage(snapshot, stage);
            VerifyStagedCurrent(stage);
            if (Directory.Exists(current))
            {
                ValidateDirectory(current, "current directory");
                Directory.Move(current, failed);
                movedOld = true;
            }

            beforeActivate?.Invoke();
            Directory.Move(stage, current);
            activated = true;
            RestoreRootFileIfCaptured(snapshot, root, UpdateBackupName, "Update.exe");
            afterFirstRootFile?.Invoke();
            RestoreRootFileIfCaptured(snapshot, root, StubBackupName, "CycleArc.exe");

            if (movedOld)
                TryDeleteDirectoryBestEffort(failed, root);
            TryDeleteDirectoryBestEffort(stage, root);
            TryDeleteDirectoryBestEffort(rootRollback, root);
        }
        catch
        {
            if (activated || movedOld)
                TryRemoveCurrentForRollback(current, root);
            if (movedOld && Directory.Exists(failed) && !Directory.Exists(current))
            {
                try { Directory.Move(failed, current); }
                catch { /* Preserve failed and snapshot directories for manual recovery. */ }
            }

            try { RestoreRootFilesFromRollback(root, rootRollback, rootFiles); }
            catch { /* Preserve the external snapshot and local evidence for manual recovery. */ }

            // Deliberately leave stage/failed behind on failure. They are path-bounded and
            // are useful evidence for recovery; the parent supervisor owns later cleanup.
            throw;
        }
    }

    private void CopyCurrentToStage(string snapshot, string stage)
    {
        Directory.CreateDirectory(stage);
        var records = ReadSnapshotRecords();
        foreach (var record in records)
        {
            if (IsRootBackup(record.RelativePath))
                continue;
            var source = Path.Combine(snapshot, record.RelativePath);
            var destination = Path.Combine(stage, record.RelativePath);
            var parent = Path.GetDirectoryName(destination)
                ?? throw new InvalidDataException("The recovery staging path is invalid.");
            Directory.CreateDirectory(parent);
            File.Copy(source, destination, overwrite: false);
        }
    }

    private void VerifyStagedCurrent(string stage)
    {
        var expected = ReadSnapshotRecords()
            .Where(record => !IsRootBackup(record.RelativePath))
            .ToDictionary(record => record.RelativePath, StringComparer.OrdinalIgnoreCase);
        var actualSources = EnumerateFilesForRoot(stage);
        ValidateSourceInventoryBounds(actualSources);
        var actual = HashSources(actualSources).ToDictionary(
            file => file.RelativePath,
            file => file,
            StringComparer.OrdinalIgnoreCase);
        if (actual.Count != expected.Count || actual.Keys.Any(path => !expected.ContainsKey(path)))
            throw new InvalidDataException("The recovery staging tree is incomplete.");
        foreach (var record in expected.Values)
        {
            var file = actual[record.RelativePath];
            if (file.Size != record.Size || !file.Sha256!.Equals(record.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The recovery staging checksum does not match.");
        }
    }

    private static void RestoreRootFileIfCaptured(
        string snapshot,
        string installationRoot,
        string snapshotName,
        string targetName)
    {
        var source = Path.Combine(snapshot, snapshotName);
        if (!File.Exists(source))
            return;
        var target = Path.Combine(installationRoot, targetName);
        ValidateRootTarget(target);
        File.Copy(source, target, overwrite: true);
    }

    private static IReadOnlyList<RootFileRollback> CaptureRootFiles(string root, string rollbackDirectory)
    {
        if (File.Exists(rollbackDirectory))
            throw new InvalidDataException("The recovery root rollback path is a file.");
        Directory.CreateDirectory(rollbackDirectory);
        ValidateDirectory(rollbackDirectory, "recovery root rollback directory");

        var result = new List<RootFileRollback>(2);
        long totalSize = 0;
        foreach (var targetName in new[] { "Update.exe", "CycleArc.exe" })
        {
            var target = Path.Combine(root, targetName);
            ValidateRootTarget(target);
            if (!File.Exists(target))
            {
                result.Add(new RootFileRollback(targetName, Path.Combine(rollbackDirectory, targetName), false));
                continue;
            }

            var size = new FileInfo(target).Length;
            EnsureSourceCanBeAdded(result.Count, totalSize, size);
            totalSize += size;
            var backup = Path.Combine(rollbackDirectory, targetName);
            File.Copy(target, backup, overwrite: false);
            result.Add(new RootFileRollback(targetName, backup, true));
        }

        return result;
    }

    private static void RestoreRootFilesFromRollback(
        string root,
        string rollbackDirectory,
        IReadOnlyList<RootFileRollback> files)
    {
        foreach (var file in files)
        {
            var target = Path.Combine(root, file.TargetName);
            ValidateRootTarget(target);
            if (file.Existed)
            {
                if (!File.Exists(file.BackupPath))
                    throw new InvalidDataException("The root rollback file is missing.");
                File.Copy(file.BackupPath, target, overwrite: true);
            }
            else if (File.Exists(target))
            {
                File.Delete(target);
            }
        }
    }

    private static void ValidateRootTarget(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The recovery root file is redirected.");
            if ((attributes & FileAttributes.Directory) != 0)
                throw new InvalidDataException("The recovery root file path is a directory.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static void TryRemoveCurrentForRollback(string current, string root)
    {
        try { TryDeleteDirectory(current, root); }
        catch { /* Do not replace a failure with an unsafe deletion attempt. */ }
    }

    private static void TryDeleteDirectoryBestEffort(string path, string root)
    {
        try { TryDeleteDirectory(path, root); }
        catch { /* Cleanup must not turn a completed restore into a failed restore. */ }
    }

    private static List<RecoverySource> EnumerateFilesForRoot(string root)
    {
        var list = new List<RecoverySource>();
        var directoryCount = 0;
        long totalSize = 0;
        Visit(root, "", depth: 0);
        return list;

        void Visit(string directory, string relativeDirectory, int depth)
        {
            if (depth > MaximumTraversalDepth)
                throw new InvalidDataException("Recovery paths are too deeply nested.");
            if (++directoryCount > MaximumDirectoryCount)
                throw new InvalidDataException("The recovery tree contains too many directories.");
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Recovery paths must not contain redirected files or folders.");
                var name = Path.GetFileName(path);
                var relative = string.IsNullOrEmpty(relativeDirectory)
                    ? name : Path.Combine(relativeDirectory, name);
                if ((attributes & FileAttributes.Directory) != 0)
                    Visit(path, relative, depth + 1);
                else
                {
                    var size = new FileInfo(path).Length;
                    EnsureSourceCanBeAdded(list.Count, totalSize, size);
                    totalSize += size;
                    list.Add(new RecoverySource(path, relative, size, null));
                }
            }
        }
    }

    private static void AddOptionalRootFile(
        string root,
        string sourceName,
        string snapshotName,
        ICollection<RecoverySource> sources)
    {
        var path = Path.Combine(root, sourceName);
        if (!File.Exists(path))
            return;
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Recovery root files must not be redirected.");
        var size = new FileInfo(path).Length;
        EnsureSourceCanBeAdded(sources.Count, sources.Sum(source => source.Size), size);
        sources.Add(new RecoverySource(path, snapshotName, size, null));
    }

    private static void ValidateSourceInventory(List<RecoverySource> sources)
    {
        ValidateSourceInventoryBounds(sources);
        if (sources.Any(source => !IsSafeRelativePath(source.RelativePath)))
            throw new InvalidDataException("The installation contains an unsafe recovery path.");
        if (!sources.Any(source => source.RelativePath.Equals("CycleArc.exe", StringComparison.OrdinalIgnoreCase))
            || !sources.Any(source => source.RelativePath.Equals("sq.version", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The installation is missing CycleArc.exe or sq.version.");
        if (sources.GroupBy(source => source.RelativePath, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() != 1))
            throw new InvalidDataException("The installation contains duplicate recovery paths.");
    }

    private static void ValidateSourceInventoryBounds(List<RecoverySource> sources)
    {
        if (sources.Count is < 2 or > MaximumFileCount)
            throw new InvalidDataException("The installation file inventory is outside the supported limit.");
        long total = 0;
        foreach (var source in sources)
        {
            if (source.Size < 0 || source.Size > MaximumTotalSize || source.Size > MaximumTotalSize - total)
                throw new InvalidDataException("The installation is larger than the supported recovery limit.");
            total += source.Size;
        }
    }

    private static void EnsureSourceCanBeAdded(int count, long totalSize, long size)
    {
        if (count >= MaximumFileCount)
            throw new InvalidDataException("The installation file inventory is outside the supported limit.");
        if (size < 0 || size > MaximumTotalSize || size > MaximumTotalSize - totalSize)
            throw new InvalidDataException("The installation is larger than the supported recovery limit.");
    }

    private static List<RecoverySource> HashSources(List<RecoverySource> sources)
    {
        // All count, size, path, depth, and reparse-point limits are checked by the caller before
        // this method runs. This keeps an oversized or hostile tree from being read into hashes.
        return sources
            .Select(source => source with { Sha256 = HashFile(source.SourcePath) })
            .ToList();
    }

    private IEnumerable<UpdateRecoveryFile> ReadSnapshotRecords()
    {
        return Files;
    }

    private static UpdateRecoveryFile RequireRecord(
        IReadOnlyDictionary<string, UpdateRecoveryFile> records,
        string name)
    {
        if (!records.TryGetValue(name, out var record))
            throw new InvalidDataException($"The recovery snapshot is missing {name}.");
        return record;
    }

    private static void ValidateSnapshotDirectories(string snapshot, IEnumerable<string> records)
    {
        var expected = records
            .SelectMany(path => ParentDirectories(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in EnumerateDirectories(snapshot))
        {
            if (!expected.Contains(path))
                throw new InvalidDataException("The recovery snapshot contains an unrecorded directory.");
        }
    }

    private static IEnumerable<string> ParentDirectories(string relativePath)
    {
        var directory = Path.GetDirectoryName(relativePath);
        while (!string.IsNullOrEmpty(directory))
        {
            yield return directory;
            directory = Path.GetDirectoryName(directory);
        }
    }

    private static IEnumerable<string> EnumerateDirectories(string root)
    {
        var directories = new List<string>();
        var directoryCount = 0;
        Visit(root, "", depth: 0);
        return directories;

        void Visit(string directory, string relativeDirectory, int depth)
        {
            if (depth > MaximumTraversalDepth)
                throw new InvalidDataException("Recovery paths are too deeply nested.");
            if (++directoryCount > MaximumDirectoryCount)
                throw new InvalidDataException("The recovery tree contains too many directories.");
            foreach (var path in Directory.EnumerateDirectories(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Recovery paths must not contain redirected files or folders.");
                var name = Path.GetFileName(path);
                var relative = string.IsNullOrEmpty(relativeDirectory)
                    ? name : Path.Combine(relativeDirectory, name);
                directories.Add(relative);
                Visit(path, relative, depth + 1);
            }
        }
    }

    private static void ValidateDirectory(string path, string description)
    {
        if (!Directory.Exists(path))
            throw new InvalidDataException($"The {description} is missing.");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"The {description} is redirected.");
    }

    private static void ValidateRootDirectory(string path, string description)
    {
        ValidateDirectory(path, description);
    }

    private static void EnsureSnapshotDestination(string snapshot)
    {
        if (File.Exists(snapshot))
            throw new InvalidDataException("The recovery snapshot path is a file.");
        if (Directory.Exists(snapshot))
        {
            ValidateDirectory(snapshot, "snapshot directory");
            if (Directory.EnumerateFileSystemEntries(snapshot).Any())
                throw new InvalidDataException("The recovery snapshot directory is not empty.");
        }
    }

    private static void EnsureRootsDoNotOverlap(string installationRoot, string snapshotDirectory)
    {
        if (PathsOverlap(installationRoot, snapshotDirectory))
            throw new InvalidDataException("The recovery snapshot must be outside the installation root.");
    }

    private static bool PathsOverlap(string first, string second) =>
        IsWithin(first, second) || IsWithin(second, first);

    private static bool IsWithin(string path, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return path.Equals(root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException($"The {description} is required.", nameof(path));
        try { return Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        { throw new InvalidDataException($"The {description} is invalid.", exception); }
    }

    private static void ValidateVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidDataException("The recovery version is required.");
    }

    private static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathFullyQualified(path)
            || path.Contains(':') || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return false;
        var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (normalized.Equals(".", StringComparison.Ordinal) || normalized.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return false;
        return normalized.Split(Path.DirectorySeparatorChar)
            .All(part => part.Length > 0 && part is not "." and not ".." && part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
    }

    private static bool IsRootBackup(string relativePath) =>
        relativePath.Equals(UpdateBackupName, StringComparison.OrdinalIgnoreCase)
        || relativePath.Equals(StubBackupName, StringComparison.OrdinalIgnoreCase);

    private static bool IsSha256(string value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, options: FileOptions.SequentialScan);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    private static void TryDeleteDirectory(string path, string root)
    {
        if (!IsWithin(path, root) || path.Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The recovery cleanup path is outside the installation root.");
        if (!Directory.Exists(path))
            return;
        ValidateDirectory(path, "recovery directory");
        Directory.Delete(path, recursive: true);
    }

    private sealed record RecoverySource(string SourcePath, string RelativePath, long Size, string? Sha256);

    private sealed record RootFileRollback(string TargetName, string BackupPath, bool Existed);
}
