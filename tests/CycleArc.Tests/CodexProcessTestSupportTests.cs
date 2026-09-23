using Xunit.Sdk;
using static CycleArc.Tests.CodexProcessTestSupport;

namespace CycleArc.Tests;

public class CodexProcessTestSupportTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CleanupTimeout_ReportsFailuresAndRunsRemainingSteps(bool bodyFails)
    {
        var primary = new XunitException("protocol-completion; initialize-received pid=123");
        var disposalFailure = new IOException("synthetic fixture disposal failure");
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var diagnostics = new List<string>();
        var lastStepRan = false;

        var failure = await Record.ExceptionAsync(() => RunWithCleanupAsync(
            () => bodyFails ? Task.FromException(primary) : Task.CompletedTask,
            diagnostics.Add,
            ("protocol-completion", () => pending.Task.WaitAsync(TimeSpan.Zero)),
            ("fixture-dispose", () => Task.FromException(disposalFailure)),
            ("last-step", () => { lastStepRan = true; return Task.CompletedTask; })));

        Assert.True(lastStepRan);
        Assert.Collection(diagnostics,
            message =>
            {
                Assert.Contains("protocol-completion", message);
                Assert.Contains(nameof(TimeoutException), message);
            },
            message =>
            {
                Assert.Contains("fixture-dispose", message);
                Assert.Contains(disposalFailure.Message, message);
            });
        if (bodyFails)
        {
            Assert.Same(primary, failure);
            Assert.Contains(primary.Message, failure.Message);
        }
        else
        {
            var aggregate = Assert.IsType<AggregateException>(failure);
            Assert.Collection(aggregate.InnerExceptions,
                error => Assert.IsType<TimeoutException>(error),
                error => Assert.Same(disposalFailure, error));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("missing-directory")]
    public void MissingNode_FailsExplicitly(string searchPath)
    {
        var failure = Assert.Throws<TrueException>(() => RequireNode(
            "fake-codex-app-server.js", searchPath));
        Assert.Contains("node.exe must be on PATH", failure.Message);
    }

    [Theory]
    [InlineData("fake-codex-app-server.js")]
    [InlineData("hang-codex-app-server.js")]
    public void MissingFixture_FailsWithRequiredPath(string fixtureName)
    {
        var root = Path.Combine(Path.GetTempPath(), $"cyclearc-missing-fixture-{Guid.NewGuid():N}");
        var failure = Assert.Throws<TrueException>(() => RequireNode(fixtureName, "", root));
        Assert.Contains("Required Codex process fixture is missing", failure.Message);
        Assert.Contains(Path.Combine(root, "Fixtures", fixtureName), failure.Message);
    }
}
