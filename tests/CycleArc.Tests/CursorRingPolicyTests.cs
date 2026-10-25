using System.Text;
using System.Text.Json;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Providers.Cursor;
using CycleArc.Providers.Usage;
using CycleArc.Services;

namespace CycleArc.Tests;

/// <summary>
/// The representative Cursor ring follows the documented pool of the latest verified hook
/// request. Snapshots come from the production usage-summary/Sand parsers and receipts from the
/// production hook parser, inbox merge and account attribution.
/// </summary>
public sealed class CursorRingPolicyTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 4, 0, 0, TimeSpan.Zero);
    private const string Email = "person@example.com";

    public CursorRingPolicyTests() => UiText.SetLanguage(UiLanguage.English);

    public void Dispose() => UiText.SetLanguage(UiLanguage.English);

    // Cursor Models 100% left, Other Models 95% left, Grok Bot 80% left.
    private const string Summary = """
        {"billingCycleStart":"2026-10-01T00:00:00Z","billingCycleEnd":"2026-11-01T00:00:00Z","membershipType":"pro","limitType":"user",
         "individualUsage":{"plan":{"enabled":true,"used":500,"limit":2000,"remaining":1500,"autoPercentUsed":0,"apiPercentUsed":5,"totalPercentUsed":2},
         "onDemand":{"enabled":false,"used":0,"limit":null,"remaining":null}},"teamUsage":{}}
        """;

    private const string Sand = """
        {"usagePercent":20,"hasAvailableUsage":true,"includedLimitZero":false,"hasNonZeroIncludedLimit":true,"nextResetTimestampUtc":"2026-10-27T00:00:00Z"}
        """;

    private static CodexQuotaSnapshot Snapshot(string summary = Summary, bool sand = true,
        CodexQuotaStatus status = CodexQuotaStatus.Available, string? detail = CursorUsagePresentation.LiveDetail)
    {
        using var document = JsonDocument.Parse(summary);
        var windows = CursorUsageClient.ParseUsage(document.RootElement, Now.AddMinutes(-2)).Windows.ToList();
        if (sand)
        {
            using var sandDocument = JsonDocument.Parse(Sand);
            if (CursorUsageClient.ParseSand(sandDocument.RootElement, Now.AddMinutes(-2)) is { } grok) windows.Add(grok);
        }
        return new CodexQuotaSnapshot(status, "pro", Now.AddMinutes(-2), Now.AddMinutes(-2), null, null, null, windows, detail)
        {
            Provider = UsageProviderId.Cursor
        };
    }

    // Shape of a real Cursor 3.24 beforeSubmitPrompt / stop payload (top-level fields only).
    private static string Submit(string? modelId, string? model, string generation, string email = Email) => $$"""
        {"conversation_id":"c","generation_id":"{{generation}}","model":{{Json(model)}},"model_id":{{Json(modelId)}},
         "model_params":[{"id":"effort","value":"medium"}],"hook_event_name":"beforeSubmitPrompt","cursor_version":"3.24.12",
         "workspace_roots":["C:/p"],"user_email":"{{email}}","transcript_path":null,"prompt":"secret","attachments":[]}
        """;

    private static string Stop(string modelId, string generation, string email = Email) => $$"""
        {"conversation_id":"c","generation_id":"{{generation}}","model":"{{modelId}}","model_id":"{{modelId}}",
         "hook_event_name":"stop","user_email":"{{email}}","status":"completed","loop_count":0}
        """;

    private static string Json(string? value) => value is null ? "null" : "\"" + value + "\"";

    private static CursorActivityState Receive(CursorActivityState? state, string json, DateTimeOffset at) =>
        CursorActivityStore.Merge(state, CursorHookEvent.Parse(Encoding.UTF8.GetBytes(json), at)!).State;

    private static CodexAccountView Account(string id = "cursor-a", string email = Email, CodexQuotaSnapshot? snapshot = null) =>
        new(new CodexAccountProfile(id, "", "Cursor " + id) { Provider = UsageProviderId.Cursor }, snapshot ?? Snapshot(), email)
        {
            IsConnected = true
        };

    private static CodexAccountView Attached(CodexAccountView account, CursorActivityState? state,
        CursorActivityIntegration integration = CursorActivityIntegration.Connected) =>
        CursorActivityAttribution.Attach([account], integration, state)[0];

    private static CodexAccountView WithRequest(string? modelId, string? model = null, DateTimeOffset? at = null,
        CodexQuotaSnapshot? snapshot = null) =>
        Attached(Account(snapshot: snapshot), Receive(null, Submit(modelId, model ?? modelId, "g1"), at ?? Now.AddMinutes(-1)));

    private static WidgetAccountModel Widget(CodexAccountView account) =>
        WidgetAccountModel.From(account, true, UsagePeriodPreference.Auto, Now);

    private static CodexQuotaWindow Pool(CodexQuotaSnapshot snapshot, string limitId) =>
        snapshot.Windows.Single(window => window.LimitId == limitId);

    // The popup's ring and the limit row placed beside it, as FlyoutWindow binds them.
    private static (CodexRingPresentation Ring, IReadOnlyList<CodexDisplayRow> Beside, CodexDisplayRow? Recent) Popup(CodexAccountView account)
    {
        var link = CursorRingPolicy.Resolve(account.Snapshot, account.CursorActivity, Now);
        var ring = CodexRingPresentation.FromDetail(account.Snapshot, UsagePeriodPreference.Auto, link.Window);
        var detail = account.Snapshot with
        {
            Windows = account.Snapshot.Windows.Where(window => window.LimitId != "cursor-on-demand").ToArray()
        };
        var (primary, _, _) = CodexDisplayFormatting.DetailSections(detail, ring.Window, Now);
        return (ring, primary, CursorActivityPresentation.Row(account.CursorActivity, link, ring.Window?.LimitId, Now));
    }

    [Theory]
    [InlineData(UiLanguage.English)]
    [InlineData(UiLanguage.Korean)]
    public void ClaudeRequestShowsOtherModelsNinetyFivePercentEverywhereWithoutChangingValues(UiLanguage language)
    {
        UiText.SetLanguage(language);
        var plain = Account();
        var account = Attached(plain, Receive(null, Submit("claude-opus-5-5", "claude-opus-5-5", "g1"), Now.AddMinutes(-1)));
        var apiWindow = Pool(account.Snapshot, "cursor-api");

        var baseline = Widget(Attached(plain, null));
        var widget = Widget(account);
        Assert.Equal("cursor-auto", baseline.Ring.Window!.LimitId);
        Assert.Same(apiWindow, widget.Ring.Window);
        Assert.Equal(95, widget.Ring.RemainingPercent);
        Assert.Equal("95%", widget.RingRemainingValueText);
        Assert.Equal("Other Models", widget.RingTargetLabel);
        Assert.StartsWith("Other Models", widget.Ring.RemainingSubLabel, StringComparison.Ordinal);
        Assert.Equal(UsageRingBand.Normal, widget.Ring.Band);
        // Every limit keeps its own value, bar and order; only the highlighted line moves.
        Assert.Equal(baseline.Periods.Select(p => (p.PeriodLabel, p.DisplayRemainingText, p.RemainingPercent, p.ResetText)),
            widget.Periods.Select(p => (p.PeriodLabel, p.DisplayRemainingText, p.RemainingPercent, p.ResetText)));
        Assert.Equal(["Cursor Models", "Other Models", "Grok Bot"], widget.Periods.Select(p => p.PeriodLabel));
        Assert.Equal([false, true, false], widget.Periods.Select(p => p.IsRepresentative));
        Assert.Contains(UiText.T("Ring target: Other Models · from the last request's model", "링 대상: Other Models · 최근 요청 모델 기준"),
            widget.Tooltip, StringComparison.Ordinal);
        Assert.Contains("claude-opus-5-5", widget.Tooltip, StringComparison.Ordinal);
        Assert.Contains(CursorUsagePresentation.QuotaDisplayLabel("cursor-auto"), widget.Tooltip, StringComparison.Ordinal);
        Assert.Contains(CursorUsagePresentation.QuotaDisplayLabel("cursor-sand"), widget.Tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("Claude 95", widget.Tooltip, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("claude-opus-5-5 95", widget.Tooltip, StringComparison.OrdinalIgnoreCase);
        Assert.Same(plain.Snapshot, account.Snapshot);

        var (ring, beside, recent) = Popup(account);
        Assert.Same(apiWindow, ring.Window);
        Assert.Equal(widget.Ring.RemainingPercent, ring.RemainingPercent);
        Assert.Contains("Other Models", beside.Single().Label, StringComparison.Ordinal);
        Assert.Equal("claude-opus-5-5", recent!.Value);
        Assert.Contains(UiText.T("Ring shows Other Models", "링: Other Models 표시"), recent.Detail!, StringComparison.Ordinal);
        Assert.Contains(UiText.T("Cursor's plan documentation places this model in Other Models · Monthly",
            "Cursor 요금제 문서상 이 모델은 Other Models · 월간 한도를 사용"), recent.Tooltip!, StringComparison.Ordinal);
        Assert.Contains(UiText.T("does not confirm billing", "실제 과금 경로를 확정하지 않습니다"), recent.Tooltip!, StringComparison.Ordinal);
        Assert.Contains(DisplayFormatting.FormatStamp(account.CursorActivity!.Latest!.ReceivedAt), recent.Tooltip!, StringComparison.Ordinal);

        var overview = UsageAccountOverview.Create([account], account.Profile.Id);
        Assert.Same(apiWindow, overview.RepresentativeWindow(Now));
        Assert.Same(apiWindow, CodexRingPresentation.For(account, UsagePeriodPreference.Auto, Now).Window);
    }

    [Fact]
    public void RingFollowsEachNewRequestBetweenClaudeAndGrokWithoutAServerRead()
    {
        var snapshot = Snapshot();
        var plain = Account(snapshot: snapshot);
        var steps = new[]
        {
            ("claude-opus-5-5", "cursor-api"),
            ("grok-4.7", "cursor-auto"),
            ("claude-sonnet-5-5", "cursor-api"),
            ("composer-2.5", "cursor-auto"),
            ("gpt-5.6-sol", "cursor-api")
        };
        CursorActivityState? state = null;
        for (var i = 0; i < steps.Length; i++)
        {
            var (model, limit) = steps[i];
            state = Receive(state, Submit(model, model, "g" + i), Now.AddMinutes(-30 + i));
            var account = Attached(plain, state);
            Assert.Same(snapshot, account.Snapshot);
            Assert.Equal(limit, Widget(account).Ring.Window!.LimitId);
            Assert.Equal(limit, Popup(account).Ring.Window!.LimitId);
            Assert.Equal(limit, CodexRingPresentation.For(account, UsagePeriodPreference.Auto, Now).Window!.LimitId);
        }
    }

    [Theory]
    [InlineData("claude-opus-5-5", CursorUsagePool.OtherModels)]
    [InlineData("claude-4.5-sonnet", CursorUsagePool.OtherModels)]
    [InlineData("claude-4.5-sonnet-thinking", CursorUsagePool.OtherModels)]
    [InlineData("claude-opus-4-7-thinking-max", CursorUsagePool.OtherModels)]
    [InlineData("Claude-Sonnet-5.5", CursorUsagePool.OtherModels)]
    [InlineData("claude-fable-5-1", CursorUsagePool.OtherModels)]
    [InlineData("gpt-5", CursorUsagePool.OtherModels)]
    [InlineData("gpt-5.6-sol-high-fast", CursorUsagePool.OtherModels)]
    [InlineData("gpt-5.1-codex-max", CursorUsagePool.OtherModels)]
    [InlineData("gemini-3.1-pro", CursorUsagePool.OtherModels)]
    [InlineData("gemini-4-argon", CursorUsagePool.OtherModels)]
    [InlineData("kimi-k3", CursorUsagePool.OtherModels)]
    [InlineData("composer-2.5", CursorUsagePool.CursorModels)]
    [InlineData("composer-2.5-fast", CursorUsagePool.CursorModels)]
    [InlineData("grok-4.7", CursorUsagePool.CursorModels)]
    [InlineData("grok-4.7-500k-fast", CursorUsagePool.CursorModels)]
    [InlineData("cursor-grok-4.6-high", CursorUsagePool.CursorModels)]
    [InlineData("grok-4-5", CursorUsagePool.CursorModels)]
    [InlineData("auto", CursorUsagePool.Routed)]
    [InlineData("Auto", CursorUsagePool.Routed)]
    [InlineData("default", CursorUsagePool.Routed)]
    [InlineData("cursor-router", CursorUsagePool.Routed)]
    [InlineData("grok-code-fast-1", CursorUsagePool.Unknown)]
    [InlineData("grok-4", CursorUsagePool.Unknown)]
    [InlineData("grok-3-mini", CursorUsagePool.Unknown)]
    [InlineData("grok-bot", CursorUsagePool.Unknown)]
    [InlineData("cursor-sand", CursorUsagePool.Unknown)]
    [InlineData("cursor", CursorUsagePool.Unknown)]
    [InlineData("o3", CursorUsagePool.Unknown)]
    [InlineData("deepseek-v3.1", CursorUsagePool.Unknown)]
    [InlineData("anthropic/claude-opus", CursorUsagePool.Unknown)]
    [InlineData("claudette", CursorUsagePool.Unknown)]
    [InlineData("", CursorUsagePool.Unknown)]
    [InlineData("   ", CursorUsagePool.Unknown)]
    [InlineData(null, CursorUsagePool.Unknown)]
    public void ModelNamesMapOnlyToTheirDocumentedPool(string? model, CursorUsagePool expected) =>
        Assert.Equal(expected, CursorModelPools.Classify(model));

    [Fact]
    public void StructuredModelIdDecidesAndTheLegacySlugIsOnlyAFallback()
    {
        Assert.Equal(CursorUsagePool.OtherModels, CursorModelPools.Classify("claude-opus-5-5", "composer-2.5"));
        Assert.Equal(CursorUsagePool.Routed, CursorModelPools.Classify("default", "claude-opus-5-5"));
        Assert.Equal(CursorUsagePool.Unknown, CursorModelPools.Classify("new-model-x", "claude-opus-5-5"));
        Assert.Equal(CursorUsagePool.CursorModels, CursorModelPools.Classify(null, "composer-2.5"));
        Assert.Equal(CursorUsagePool.OtherModels, CursorModelPools.Classify(" ", "gpt-5"));
        Assert.Equal(CursorUsagePool.Unknown, CursorModelPools.Classify(null, null));

        // model_id missing in the hook payload: the receipt keeps the slug and the ring uses it.
        var slugOnly = WithRequest(null, "gemini-3.1-pro");
        Assert.Null(slugOnly.CursorActivity!.Latest!.ModelId);
        Assert.Equal("cursor-api", Widget(slugOnly).Ring.Window!.LimitId);
    }

    [Theory]
    [InlineData("auto", CursorRingLinkState.Routed)]
    [InlineData("default", CursorRingLinkState.Routed)]
    [InlineData("grok-code-fast-1", CursorRingLinkState.Unconfirmed)]
    [InlineData("some-future-model", CursorRingLinkState.Unconfirmed)]
    [InlineData(null, CursorRingLinkState.Unconfirmed)]
    public void AutoUnknownOrMissingModelsKeepTheRepresentativeLimit(string? model, CursorRingLinkState state)
    {
        var account = WithRequest(model, model);
        var link = CursorRingPolicy.Resolve(account.Snapshot, account.CursorActivity, Now);
        Assert.Equal(state, link.State);
        Assert.Null(link.Window);
        var widget = Widget(account);
        Assert.Equal("cursor-auto", widget.Ring.Window!.LimitId);
        Assert.Equal("Cursor Models", widget.RingTargetLabel);
        Assert.Contains("Ring target: Cursor Models", widget.Tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("from the last request's model", widget.Tooltip, StringComparison.Ordinal);
        var recent = Popup(account).Recent!;
        Assert.Contains(CursorActivityPresentation.LinkText(link), recent.Detail!, StringComparison.Ordinal);
        Assert.Contains("The ring keeps showing Cursor Models · Monthly, the representative limit.", recent.Tooltip!, StringComparison.Ordinal);
    }

    [Fact]
    public void HookOffDisconnectedUnverifiedOrNoReceiptNeverSwitches()
    {
        var state = Receive(null, Submit("claude-opus-5-5", "claude-opus-5-5", "g1"), Now.AddMinutes(-1));
        var cases = new[]
        {
            Attached(Account(), state, CursorActivityIntegration.Off),
            Attached(Account(), state, CursorActivityIntegration.Disconnected),
            Attached(Account(email: "someone-else@example.com"), state),
            Attached(Account(), null),
            Account() with { CursorActivity = new(CursorActivityIntegration.Connected, false, null) },
            Account() with
            {
                // A view that claims a receipt without verification is still ignored.
                CursorActivity = new(CursorActivityIntegration.Connected, false,
                    new CursorRecentActivity(CursorActivityKind.Request, "claude-opus-5-5", "claude-opus-5-5", null, null, Now))
            }
        };
        foreach (var account in cases)
        {
            Assert.Equal(CursorRingLinkState.NoReceipt, CursorRingPolicy.Resolve(account.Snapshot, account.CursorActivity, Now).State);
            Assert.Equal("cursor-auto", Widget(account).Ring.Window!.LimitId);
            Assert.Equal("cursor-auto", Popup(account).Ring.Window!.LimitId);
            Assert.Null(UsageAccountOverview.Create([account], account.Profile.Id).RepresentativeWindow(Now));
        }
    }

    [Fact]
    public void EachCursorAccountFollowsOnlyItsOwnRequests()
    {
        var state = Receive(null, Submit("claude-opus-5-5", "claude-opus-5-5", "a1", "a@example.com"), Now.AddMinutes(-3));
        state = Receive(state, Submit("grok-4.7", "grok-4.7", "b1", "b@example.com"), Now.AddMinutes(-2));
        var attached = CursorActivityAttribution.Attach(
            [Account("a", "a@example.com"), Account("b", "b@example.com"), Account("c", "c@example.com")],
            CursorActivityIntegration.Connected, state);

        Assert.Equal(["cursor-api", "cursor-auto", "cursor-auto"],
            WidgetAccountModel.All(attached, "a", UsagePeriodPreference.Auto, Now).Select(model => model.Ring.Window!.LimitId));
        Assert.Equal(CursorRingLinkState.Linked, CursorRingPolicy.Resolve(attached[1].Snapshot, attached[1].CursorActivity, Now).State);
        Assert.Equal(CursorRingLinkState.NoReceipt, CursorRingPolicy.Resolve(attached[2].Snapshot, attached[2].CursorActivity, Now).State);

        // The tray icon and its other-account summary use each account's own pool too.
        Assert.Equal(["95%", "100%", "100%"],
            attached.Select(account => CodexRingPresentation.For(account, UsagePeriodPreference.Auto, Now).RemainingValueText));
        Assert.Equal("cursor-api", UsageAccountOverview.Create(attached, "a").RepresentativeWindow(Now)!.LimitId);
        Assert.Null(UsageAccountOverview.Create(attached, "b").RepresentativeWindow(Now.AddHours(13)));
    }

    [Fact]
    public void AnOlderCompletionOrALateReceiptNeverOverridesTheLatestRequest()
    {
        // A Composer run completes, then a Claude request starts in another session.
        var state = Receive(null, Submit("composer-2.5", "composer-2.5", "g1"), Now.AddMinutes(-10));
        state = Receive(state, Submit("claude-opus-5-5", "claude-opus-5-5", "g2"), Now.AddMinutes(-4));
        // The earlier Composer generation's completion arrives after the Claude request.
        state = Receive(state, Stop("composer-2.5", "g1"), Now.AddMinutes(-3));
        var account = Attached(Account(), state);
        Assert.Equal("claude-opus-5-5", account.CursorActivity!.Latest!.ModelId);
        Assert.Equal("cursor-api", Widget(account).Ring.Window!.LimitId);

        // A slow hook process delivering an older Grok request is rejected by the inbox.
        var late = CursorHookEvent.Parse(Encoding.UTF8.GetBytes(Submit("grok-4.7", "grok-4.7", "g0")), Now.AddMinutes(-20))!;
        Assert.False(CursorActivityStore.Merge(state, late).Changed);

        // The Claude request's own completion keeps the same pool.
        state = Receive(state, Stop("claude-opus-5-5", "g2"), Now.AddMinutes(-1));
        account = Attached(Account(), state);
        Assert.Equal(CursorActivityKind.Completion, account.CursorActivity!.Latest!.Kind);
        Assert.Equal("cursor-api", Widget(account).Ring.Window!.LimitId);
    }

    [Fact]
    public void MissingDisabledOrUnknownPoolNeverShowsAnotherLimitAsTheModelsUsage()
    {
        // Only Cursor Models reported (no third-party pool), Other Models off, and Other Models unknown.
        var cursorOnly = Snapshot("""{"individualUsage":{"plan":{"enabled":true,"totalPercentUsed":10}}}""");
        var withApi = Snapshot();
        var disabled = withApi with
        {
            Windows = withApi.Windows.Select(window => window.LimitId == "cursor-api" ? window with { IsEnabled = false } : window).ToArray()
        };
        var unknown = Snapshot("""{"individualUsage":{"plan":{"enabled":true,"autoPercentUsed":0}}}""");
        foreach (var snapshot in new[] { cursorOnly, disabled, unknown })
        {
            var account = WithRequest("claude-opus-5-5", snapshot: snapshot);
            var link = CursorRingPolicy.Resolve(snapshot, account.CursorActivity, Now);
            Assert.Equal(CursorRingLinkState.PoolUnavailable, link.State);
            Assert.Null(link.Window);
            var widget = Widget(account);
            Assert.NotEqual("cursor-api", widget.Ring.Window?.LimitId);
            Assert.DoesNotContain("from the last request's model", widget.Tooltip, StringComparison.Ordinal);
            Assert.Contains("No Other Models value · ring unchanged", widget.Tooltip, StringComparison.Ordinal);
            Assert.Contains("Another limit is not shown as this model's usage.", Popup(account).Recent!.Tooltip!, StringComparison.Ordinal);
        }

        // Grok never falls back to Grok Bot's weekly allowance.
        var sandOnly = Snapshot("""{"individualUsage":{"plan":{"enabled":true,"apiPercentUsed":5}}}""");
        var grok = WithRequest("grok-4.7", snapshot: sandOnly);
        Assert.Equal(CursorRingLinkState.PoolUnavailable, CursorRingPolicy.Resolve(sandOnly, grok.CursorActivity, Now).State);
        Assert.NotEqual("cursor-sand", Widget(grok).Ring.Window?.LimitId);
    }

    [Fact]
    public void StaleReceiptsExpireButAStaleQuotaKeepsItsLinkAndItsStaleMarker()
    {
        foreach (var at in new[] { Now - CursorRingPolicy.ReceiptLifetime - TimeSpan.FromMinutes(1), Now.AddHours(1) })
        {
            var old = WithRequest("claude-opus-5-5", at: at);
            Assert.Equal(CursorRingLinkState.Expired, CursorRingPolicy.Resolve(old.Snapshot, old.CursorActivity, Now).State);
            Assert.Equal("cursor-auto", Widget(old).Ring.Window!.LimitId);
            Assert.Contains("Not recent · ring unchanged", Widget(old).Tooltip, StringComparison.Ordinal);
        }
        var edge = WithRequest("claude-opus-5-5", at: Now - CursorRingPolicy.ReceiptLifetime);
        Assert.Equal("cursor-api", Widget(edge).Ring.Window!.LimitId);
        Assert.Equal(Now, CursorRingPolicy.LinkExpiresAt(edge.CursorActivity));

        var stale = WithRequest("claude-opus-5-5", snapshot: Snapshot(status: CodexQuotaStatus.Stale, detail: "cursor-live-request-failed"));
        var model = Widget(stale);
        Assert.Equal("cursor-api", model.Ring.Window!.LimitId);
        Assert.True(model.IsStale);
        Assert.Equal(95, model.Ring.RemainingPercent);
    }

    [Theory]
    [InlineData("cursor-identity-mismatch")]
    [InlineData("cursor-live-identity-mismatch")]
    public void MismatchedOrSignedOutAccountsGetNoLinkAndNoQuota(string detail)
    {
        var state = Receive(null, Submit("claude-opus-5-5", "claude-opus-5-5", "g1"), Now.AddMinutes(-1));
        var mismatch = Account(snapshot: Snapshot(detail: detail));
        // Even a projection that carries a receipt is ignored for a protected snapshot.
        var forged = mismatch with
        {
            CursorActivity = new(CursorActivityIntegration.Connected, true, CursorActivityAttribution.Latest(state,
                CursorActivityAttribution.AccountKey(Email)!))
        };
        Assert.Null(Attached(mismatch, state).CursorActivity!.Latest);
        Assert.Equal(CursorRingLinkState.NoReceipt, CursorRingPolicy.Resolve(forged.Snapshot, forged.CursorActivity, Now).State);
        Assert.Null(Widget(forged).Ring.Window);
        Assert.Empty(Widget(forged).Periods);

        var signedOut = Account(snapshot: Snapshot(status: CodexQuotaStatus.SignedOut)) with { CursorActivity = forged.CursorActivity };
        Assert.Equal(CursorRingLinkState.NoReceipt, CursorRingPolicy.Resolve(signedOut.Snapshot, signedOut.CursorActivity, Now).State);
    }

    [Fact]
    public void CodexAndClaudeRingsAndAllAlertsAreUnchanged()
    {
        var receipt = new CursorActivityView(CursorActivityIntegration.Connected, true,
            new CursorRecentActivity(CursorActivityKind.Request, "claude-opus-5-5", "claude-opus-5-5", null, null, Now));
        var codexSnapshot = new CodexQuotaSnapshot(CodexQuotaStatus.Available, "plus", Now, Now, true, null, null,
            [new CodexQuotaWindow("codex", 40, 300, Now.AddHours(2), CodexWindowKind.FiveHour),
             new CodexQuotaWindow("codex", 70, 10_080, Now.AddDays(3), CodexWindowKind.Weekly)], null);
        var claudeSnapshot = codexSnapshot with { Provider = UsageProviderId.Claude, TechnicalDetail = "claude-live" };
        foreach (var (snapshot, provider) in new[] { (codexSnapshot, UsageProviderId.Codex), (claudeSnapshot, UsageProviderId.Claude) })
        {
            var account = new CodexAccountView(new CodexAccountProfile("x", "", "X") { Provider = provider }, snapshot, Email)
            {
                IsConnected = true
            };
            foreach (var preference in Enum.GetValues<UsagePeriodPreference>())
            {
                Assert.Same(CodexRingPresentation.From(snapshot, preference).Window,
                    CodexRingPresentation.For(account with { CursorActivity = receipt }, preference, Now).Window);
                var plain = WidgetAccountModel.From(account, true, preference, Now);
                var withReceipt = WidgetAccountModel.From(account with { CursorActivity = receipt }, true, preference, Now);
                Assert.Same(plain.Ring.Window, withReceipt.Ring.Window);
                Assert.Equal(plain.Tooltip, withReceipt.Tooltip);
            }
            Assert.Equal(CursorRingLinkState.NoReceipt, CursorRingPolicy.Resolve(snapshot, receipt, Now).State);
        }

        // Alerts evaluate every limit of the accounts themselves; a receipt changes nothing.
        var near = Snapshot("""{"individualUsage":{"plan":{"enabled":true,"autoPercentUsed":10,"apiPercentUsed":95}}}""", sand: false);
        var cursor = Account(snapshot: near);
        var state = Receive(null, Submit("grok-4.7", "grok-4.7", "g1"), Now.AddMinutes(-1));
        var (plainAlerts, plainMarks) = UsageAlerts.Evaluate([cursor], null);
        var (linkedAlerts, linkedMarks) = UsageAlerts.Evaluate(CursorActivityAttribution.Attach([cursor],
            CursorActivityIntegration.Connected, state), null);
        Assert.Equal(plainAlerts.Select(alert => (alert.LimitName, alert.Level)), linkedAlerts.Select(alert => (alert.LimitName, alert.Level)));
        Assert.Equal(plainMarks, linkedMarks);
        Assert.NotEmpty(plainAlerts);
    }
}
