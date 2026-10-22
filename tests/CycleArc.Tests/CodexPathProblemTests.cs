using CycleArc.Codex;

namespace CycleArc.Tests;

public class CodexPathProblemTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyPath_MeansAutomaticDetection(string? path) =>
        Assert.Null(CodexExecutableLocator.ConfiguredPathProblem(path, _ => false));

    [Theory]
    [InlineData("codex.exe")]
    [InlineData(@"C:\tools\codex.txt")]
    [InlineData(@"C:\tools\codex")]
    public void RelativeOrNonCommandPaths_AreExplained(string path) =>
        Assert.NotNull(CodexExecutableLocator.ConfiguredPathProblem(path, _ => true));

    [Fact]
    public void MissingFiles_AreExplained_AndExistingOnesAccepted()
    {
        Assert.NotNull(CodexExecutableLocator.ConfiguredPathProblem(@"C:\tools\codex.exe", _ => false));
        Assert.Null(CodexExecutableLocator.ConfiguredPathProblem(@"C:\tools\codex.exe", _ => true));
        // Paths pasted from Explorer's "Copy as path" keep their quotes.
        Assert.Null(CodexExecutableLocator.ConfiguredPathProblem("\"C:\\tools\\codex.cmd\"", path => path == @"C:\tools\codex.cmd"));
    }
}
