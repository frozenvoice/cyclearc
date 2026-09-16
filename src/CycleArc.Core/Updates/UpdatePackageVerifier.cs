namespace CycleArc.Updates;

/// <summary>Verifies a downloaded full update package before it is staged for apply.</summary>
public static class UpdatePackageVerifier
{
    public const long MaximumPackageSize = 1L * 1024 * 1024 * 1024;

    public static async Task VerifyAsync(
        string packagesDirectory,
        string fileName,
        long expectedSize,
        string expectedSha256,
        CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagesDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        token.ThrowIfCancellationRequested();
        ValidateMetadata(fileName, expectedSize, expectedSha256);

        string packagePath;
        try
        {
            var directoryPath = Path.GetFullPath(packagesDirectory);
            packagePath = Path.Combine(directoryPath, fileName);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            throw new InvalidDataException("The update package path is invalid.", exception);
        }

        try
        {
            if ((File.GetAttributes(packagePath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The update package path is not a regular file.");

            await using var stream = new FileStream(
                packagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            if (stream.Length != expectedSize)
                throw new InvalidDataException("The update package size does not match its manifest.");

            var actualHash = await System.Security.Cryptography.SHA256
                .HashDataAsync(stream, token)
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    actualHash,
                    Convert.FromHexString(expectedSha256)))
                throw new InvalidDataException("The update package checksum does not match its manifest.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new InvalidDataException("The update package could not be read.", exception);
        }
    }

    private static void ValidateMetadata(string fileName, long expectedSize, string expectedSha256)
    {
        if (Path.IsPathFullyQualified(fileName)
            || fileName != Path.GetFileName(fileName)
            || fileName.Contains(Path.DirectorySeparatorChar)
            || fileName.Contains(Path.AltDirectorySeparatorChar)
            // ':' is also rejected explicitly because NTFS treats it as an alternate
            // data stream separator even when the platform path API accepts it in a name.
            || fileName.Contains(':')
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || fileName.Equals(".", StringComparison.Ordinal)
            || fileName.Equals("..", StringComparison.Ordinal))
            throw new InvalidDataException("The update package file name is invalid.");

        if (!fileName.EndsWith("-full.nupkg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update package must be a full .nupkg file.");

        if (expectedSize <= 0 || expectedSize > MaximumPackageSize)
            throw new InvalidDataException("The update package size is outside the supported range.");

        if (string.IsNullOrWhiteSpace(expectedSha256)
            || expectedSha256.Length != 64
            || expectedSha256.Any(character => !char.IsAsciiHexDigit(character)))
            throw new InvalidDataException("The update package checksum is invalid.");
    }
}
