using CycleArc.Providers.ChatGpt;
using CycleArc.Services;

namespace CycleArc.Tests;

public class ManualIncrementalSyncTests
{
    [Fact]
    public async Task UnchangedConversations_AreNotBodyFetchedOnManualRefresh()
    {
        var (engine, provider, settings, items) = CreateHarness(75);
        await engine.SyncAsync(provider, settings, SyncRunOptions.ManualIncremental);
        Assert.Equal(75, provider.BodyFetches);

        await engine.SyncAsync(provider, settings, SyncRunOptions.ManualIncremental);
        Assert.Equal(75, provider.BodyFetches);

        items[0].UpdateTime += 30;
        await engine.SyncAsync(provider, settings, SyncRunOptions.ManualIncremental);
        Assert.Equal(76, provider.BodyFetches);

        await engine.SyncAsync(
            provider,
            settings,
            new SyncRunOptions { Origin = SyncOrigin.Manual, BypassPause = true, ForceBodyRescan = true });
        Assert.Equal(151, provider.BodyFetches);
    }

    [Fact]
    public void NormalUiRefreshPaths_OnlyUseCodex()
    {
        var app = File.ReadAllText(Find("src/CycleArc/App.xaml.cs"));
        Assert.DoesNotContain("new SyncEngine", app, StringComparison.Ordinal);
        Assert.DoesNotContain("SyncRunOptions", app, StringComparison.Ordinal);
        Assert.Contains("new CodexAccountManager", app, StringComparison.Ordinal);
        Assert.Contains("_refresh = _codex.Refresh", app, StringComparison.Ordinal);
        var accounts = File.ReadAllText(Find("src/CycleArc.Core/Codex/CodexAccountManager.cs"));
        Assert.Contains("new CodexRefreshCoordinator", accounts, StringComparison.Ordinal);
        foreach (var surface in new[] { "_tray.SyncRequested", "_flyout.SyncRequested", "widget.RefreshRequested" })
            Assert.Contains(surface, app, StringComparison.Ordinal);
        Assert.Contains("RefreshCodexAsync()", app, StringComparison.Ordinal);
        Assert.DoesNotContain("_taskbarStrip", app, StringComparison.Ordinal);
    }

    private static (SyncEngine Engine, IncrementalSyncTests.CountingProvider Provider, AppSettings Settings, List<ConversationIndexItem> Items)
        CreateHarness(int count)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cyclearc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SqliteStore(Path.Combine(dir, "manual.db"));
        var models = new ModelNormalizer();
        var engine = new SyncEngine(store, new ConversationParser(models), models, new AppLog(Path.Combine(dir, "logs")));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var fixture = new FixtureChatGptProvider();
        var items = new List<ConversationIndexItem>();
        for (var i = 0; i < count; i++)
        {
            var id = $"conv-{i:000}";
            var item = new ConversationIndexItem { Id = id, UpdateTime = now - i, CreateTime = now - i - 10 };
            items.Add(item);
            fixture.AddConversation(item, ConversationFixtures.NormalPro(id, now - i));
        }

        var settings = AppSettings.CreateDefaults();
        settings.ResetTimeZoneId = "UTC";
        settings.BodyFetchDelayMilliseconds = 0;
        return (engine, new IncrementalSyncTests.CountingProvider(fixture), settings, items);
    }

    private static string Find(string relative)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException(relative);
    }
}
