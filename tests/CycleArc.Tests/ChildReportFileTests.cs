using System.Text;
using CycleArc.Services;

namespace CycleArc.Tests;

/// <summary>
/// The desktop-instance process checks have a parent polling a report file while the child
/// appends to it. These cover the file-sharing contract between those two sides and the
/// handling of a record that is only half written when the parent happens to look.
/// </summary>
public sealed class ChildReportFileTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("cyclearc-report-file-").FullName;

    private string NewReport(string? seed = null)
    {
        var path = Path.Combine(_work, Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(path, seed ?? string.Empty);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // Case A. The parent's own read handle, held open through the production helper, must not
    // deny the child's append. This is the exact overlap that loses a shutdown report.
    [Fact]
    public void Append_SucceedsWhileTheParentHoldsItsReadHandleOpen()
    {
        var path = NewReport();
        using (var parentIsReading = ChildReportFile.OpenRead(path))
        {
            ChildReportFile.Append(path, "{\"state\":\"shutdown\"}");
            Assert.True(parentIsReading.CanRead);
        }

        Assert.Equal(new[] { "{\"state\":\"shutdown\"}" }, ChildReportFile.ReadCompleteLines(path));
    }

    // The same overlap under the sharing mode File.ReadLines uses. This is what the parent
    // used to do, and it denies the child's write open for as long as the read is in flight.
    [Fact]
    public void Append_IsDeniedWhileAReaderExcludesWriters()
    {
        var path = NewReport();
        using var exclusiveOfWriters = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var error = Assert.ThrowsAny<Exception>(() => ChildReportFile.Append(path, "{\"state\":\"shutdown\"}"));
        Assert.True(error is IOException or UnauthorizedAccessException, $"Unexpected {error.GetType().Name}: {error.Message}");
    }

    // The exact pairing the desktop-instance checks used to have: the parent enumerating
    // File.ReadLines while the child opens the same file for append. File.ReadLines holds
    // FileShare.Read for the life of the enumeration, so the child's write open is refused.
    // This is the mechanism that can strand a shutdown report; it is reproduced here rather
    // than asserted from the source text.
    [Fact]
    public void LegacyReadLinesEnumeration_RefusesTheChildsAppendOpen()
    {
        var path = NewReport("{\"state\":\"ready\"}\n{\"state\":\"activate\"}\n");
        // Scoped: the enumerator holds its FileShare.Read handle until it is disposed.
        using (var enumerator = File.ReadLines(path).GetEnumerator())
        {
            Assert.True(enumerator.MoveNext(), "The report should have a first record.");

            var legacyAppend = () =>
            {
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                var payload = Encoding.UTF8.GetBytes("{\"state\":\"shutdown\"}" + Environment.NewLine);
                stream.Write(payload, 0, payload.Length);
            };
            var denied = Assert.ThrowsAny<Exception>(legacyAppend);
            Assert.True(denied is IOException or UnauthorizedAccessException,
                $"Unexpected {denied.GetType().Name}: {denied.Message}");
        }

        // The replacement read path leaves the same append free to succeed, with the
        // parent's read handle open across it.
        using (ChildReportFile.OpenRead(path))
            ChildReportFile.Append(path, "{\"state\":\"shutdown\"}");
        Assert.Contains("{\"state\":\"shutdown\"}", ChildReportFile.ReadCompleteLines(path));
    }

    // The failure is surfaced, not swallowed: callers decide what a lost report means.
    [Fact]
    public void Append_ThrowsRatherThanReportingSuccessWhenTheRecordCannotBeWritten()
    {
        var path = Path.Combine(_work, "as-a-directory");
        Directory.CreateDirectory(path);
        Assert.ThrowsAny<Exception>(() => ChildReportFile.Append(path, "{\"state\":\"shutdown\"}"));
    }

    // A record that is only partly on disk is neither a malformed record nor a real one.
    [Fact]
    public void ReadCompleteLines_WithholdsARecordThatHasNoNewlineYet()
    {
        var path = NewReport("{\"state\":\"ready\"}\n{\"state\":\"shut");
        Assert.Equal(new[] { "{\"state\":\"ready\"}" }, ChildReportFile.ReadCompleteLines(path));

        File.AppendAllText(path, "down\"}\n");
        Assert.Equal(
            new[] { "{\"state\":\"ready\"}", "{\"state\":\"shutdown\"}" },
            ChildReportFile.ReadCompleteLines(path));
    }

    // A torn multi-byte character must not decode to a replacement character inside a record
    // the reader is about to accept.
    [Fact]
    public void ReadCompleteLines_DoesNotDecodeATruncatedUtf8Sequence()
    {
        var path = NewReport();
        var complete = Encoding.UTF8.GetBytes("{\"exePath\":\"C:\\한글\\CycleArc.exe\"}\n");
        var torn = Encoding.UTF8.GetBytes("{\"exePath\":\"C:\\한글");
        File.WriteAllBytes(path, complete.Concat(torn.Take(torn.Length - 1)).ToArray());

        var lines = ChildReportFile.ReadCompleteLines(path);
        Assert.Single(lines);
        Assert.Equal("{\"exePath\":\"C:\\한글\\CycleArc.exe\"}", lines[0]);
        Assert.DoesNotContain('\uFFFD', lines[0]);
    }

    // Deterministic overlap: the reader is provably inside an open read handle when the
    // writer appends, and every record still lands exactly once.
    [Fact]
    public void ReadAndAppend_InterleaveWithoutLosingOrDuplicatingRecords()
    {
        var path = NewReport();
        const int records = 200;
        using var readerIsInsideARead = new ManualResetEventSlim(false);
        using var writerIsDone = new ManualResetEventSlim(false);
        Exception? readFailure = null;

        var reader = Task.Run(() =>
        {
            try
            {
                while (!writerIsDone.IsSet)
                {
                    using var stream = ChildReportFile.OpenRead(path);
                    readerIsInsideARead.Set();
                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    foreach (var line in ChildReportFile.ReadCompleteLines(path))
                        Assert.StartsWith("{\"n\":", line, StringComparison.Ordinal);
                }
            }
            catch (Exception ex) { readFailure = ex; }
        });

        Assert.True(readerIsInsideARead.Wait(TimeSpan.FromSeconds(10)), "The reader never opened the report.");
        for (var n = 0; n < records; n++)
            ChildReportFile.Append(path, $"{{\"n\":{n}}}");
        writerIsDone.Set();
        Assert.True(reader.Wait(TimeSpan.FromSeconds(10)), "The reader did not finish.");
        Assert.Null(readFailure);

        var final = ChildReportFile.ReadCompleteLines(path);
        Assert.Equal(records, final.Length);
        Assert.Equal(Enumerable.Range(0, records).Select(n => $"{{\"n\":{n}}}"), final);
    }
}
