using System.Text;
using CycleArc.Codex;
using CycleArc.Providers.Cursor;
using CycleArc.Providers.Usage;
using CycleArc.Services;

namespace CycleArc.Tests;

public sealed class CursorActivityTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 3, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cyclearc-cursor-activity-" + Guid.NewGuid().ToString("N"));

    public CursorActivityTests()
    {
        Directory.CreateDirectory(_root);
        UiText.SetLanguage(UiLanguage.English);
    }

    public void Dispose()
    {
        UiText.SetLanguage(UiLanguage.English);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    // Shape follows the official hooks reference: common fields plus beforeSubmitPrompt's prompt/attachments.
    private static string Submit(string? model = "claude-4.5-sonnet", string? email = "Person@Example.com",
        string generation = "gen-1", string extra = "") => $$"""
        {
          "conversation_id": "conv-1",
          "generation_id": "{{generation}}",
          "model": {{Json(model)}},
          "model_id": "claude-4.5-sonnet-thinking",
          "model_params": [{"id":"context","value":"200k"},{"id":"effort","value":"high"},{"id":"thinking","value":"true"}],
          "hook_event_name": "beforeSubmitPrompt",
          "cursor_version": "2.1.0",
          "workspace_roots": ["C:/secret/project"],
          "user_email": {{Json(email)}},
          "transcript_path": "C:/Users/p/.cursor/transcripts/conv-1.jsonl",
          "prompt": "SECRET PROMPT TEXT const apiKey = 'sk-live-123';",
          "attachments": [{"type":"file","file_path":"C:/secret/project/credentials.txt"}]{{extra}}
        }
        """;

    private static string Stop(string status = "completed", string generation = "gen-1", string? email = "person@example.com") => $$"""
        {
          "conversation_id": "conv-1",
          "generation_id": "{{generation}}",
          "model": "claude-4.5-sonnet",
          "hook_event_name": "stop",
          "cursor_version": "2.1.0",
          "workspace_roots": ["C:/secret/project"],
          "user_email": {{Json(email)}},
          "transcript_path": null,
          "status": "{{status}}",
          "loop_count": 0
        }
        """;

    private static string Json(string? value) => value is null ? "null" : "\"" + value + "\"";

    private static CursorActivityEntry? Parse(string json, DateTimeOffset? at = null) =>
        CursorHookEvent.Parse(Encoding.UTF8.GetBytes(json), at ?? Now);

    [Fact]
    public void SubmitPromptKeepsOnlyModelReasoningHashedAccountAndReceiptTime()
    {
        var entry = Parse(Submit())!;

        Assert.Equal(CursorActivityKind.Request, entry.Kind);
        Assert.Equal("claude-4.5-sonnet", entry.Model);
        Assert.Equal("claude-4.5-sonnet-thinking", entry.ModelId);
        Assert.Equal("high", entry.Effort);
        Assert.Equal("true", entry.Thinking);
        Assert.Equal(CursorActivityAttribution.AccountKey("person@example.com"), entry.AccountKey);
        Assert.Equal(64, entry.AccountKey!.Length);
        Assert.Equal(32, entry.Generation!.Length);
        Assert.Equal(Now, entry.ReceivedAt);
    }

    [Fact]
    public void StoredReceiptNeverContainsPromptAttachmentsPathsTranscriptOrRawEmail()
    {
        var store = new CursorActivityStore(_root);
        Assert.True(store.RecordAsync(Parse(Submit())!, CancellationToken.None).GetAwaiter().GetResult());

        var text = File.ReadAllText(store.Path);
        foreach (var secret in new[] { "SECRET", "sk-live", "credentials", "secret/project", "transcripts", "conv-1",
                     "gen-1", "Person@Example.com", "person@example.com", "200k", "2.1.0" })
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("claude-4.5-sonnet", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("completed", true)]
    [InlineData("aborted", false)]
    [InlineData("error", false)]
    public void OnlyCompletedStopIsACompletion(string status, bool accepted)
    {
        var entry = Parse(Stop(status));

        Assert.Equal(accepted, entry is not null);
        if (accepted) Assert.Equal(CursorActivityKind.Completion, entry!.Kind);
    }

    [Fact]
    public void MissingModelEmailAndParametersStayUnknownInsteadOfGuessed()
    {
        var entry = Parse("""{"hook_event_name":"beforeSubmitPrompt","prompt":"x"}""")!;

        Assert.Null(entry.Model);
        Assert.Null(entry.ModelId);
        Assert.Null(entry.Effort);
        Assert.Null(entry.Thinking);
        Assert.Null(entry.AccountKey);
        Assert.Null(entry.Generation);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"hook_event_name":"preToolUse","model":"x"}""")]
    [InlineData("""{"hook_event_name":"beforeSubmitPrompt","model":5}""")]
    [InlineData("""{"hook_event_name":"beforeSubmitPrompt","model":"a","model":"b"}""")]
    [InlineData("""{"hook_event_name":"beforeSubmitPrompt","hook_event_name":"stop"}""")]
    [InlineData("""{"hook_event_name":"beforeSubmitPrompt","user_email":"a@b.c","user_email":"d@e.f"}""")]
    [InlineData("""{"hook_event_name":"beforeSubmitPrompt","model_params":{}}""")]
    [InlineData("""{"hook_event_name":"beforeSubmitPrompt","prompt":"unterminated""")]
    public void MalformedOrUnrelatedInputIsRejected(string json) => Assert.Null(Parse(json));

    [Fact]
    public void UnsafeOrOversizedModelTextIsDroppedRatherThanTruncated()
    {
        var control = Parse(Submit(model: "bad\\u0007model"))!;
        var bidi = Parse(Submit(model: "gpt\\u202E5"))!;
        var longModel = Parse(Submit(model: new string('m', 129)))!;

        Assert.Null(control.Model);
        Assert.Null(bidi.Model);
        Assert.Null(longModel.Model);
        Assert.Equal("claude-4.5-sonnet-thinking", longModel.ModelId);
    }

    [Fact]
    public void UnknownFutureFieldsAreSkippedWithoutFailing()
    {
        var entry = Parse(Submit(extra: ""","future": {"nested": [1, {"deep": true}]}"""));

        Assert.NotNull(entry);
    }

    [Fact]
    public void DuplicateGenerationAndLateReceiptsNeverReplaceNewerOnes()
    {
        var first = Parse(Submit(generation: "gen-1"), Now)!;
        var (state, changed) = CursorActivityStore.Merge(null, first);
        Assert.True(changed);

        var duplicate = Parse(Submit(generation: "gen-1"), Now.AddSeconds(5))!;
        Assert.False(CursorActivityStore.Merge(state, duplicate).Changed);

        var late = Parse(Submit(model: "gpt-5", generation: "gen-0"), Now.AddSeconds(-1))!;
        Assert.False(CursorActivityStore.Merge(state, late).Changed);

        var newer = Parse(Submit(model: "gpt-5", generation: "gen-2"), Now.AddSeconds(1))!;
        var (next, accepted) = CursorActivityStore.Merge(state, newer);
        Assert.True(accepted);
        Assert.Equal("gpt-5", Assert.Single(next.Entries).Model);
    }

    [Fact]
    public void SeparateAccountsAndKindsAreKeptApartAndOldestAccountsAreDropped()
    {
        CursorActivityState? state = null;
        for (var i = 0; i < CursorActivityStore.MaxAccounts + 2; i++)
        {
            state = CursorActivityStore.Merge(state, Parse(Submit(email: $"user{i}@example.com", generation: $"g{i}"),
                Now.AddMinutes(i))!).State;
        }
        state = CursorActivityStore.Merge(state, Parse(Stop(generation: "g9", email: "user9@example.com"), Now.AddMinutes(20))!).State;

        Assert.Equal(CursorActivityStore.MaxAccounts + 1, state!.Entries.Count);
        Assert.DoesNotContain(state.Entries, entry => entry.AccountKey == CursorActivityAttribution.AccountKey("user0@example.com"));
        Assert.DoesNotContain(state.Entries, entry => entry.AccountKey == CursorActivityAttribution.AccountKey("user1@example.com"));
        var user9 = CursorActivityAttribution.AccountKey("user9@example.com");
        Assert.Equal(2, state.Entries.Count(entry => entry.AccountKey == user9));
    }

    [Fact]
    public async Task ConcurrentHookProcessesKeepTheNewestReceiptPerAccount()
    {
        var store = new CursorActivityStore(_root);
        var tasks = Enumerable.Range(0, 12).Select(i => Task.Run(() => store.RecordAsync(
            Parse(Submit(model: $"model-{i}", email: i % 2 == 0 ? "a@example.com" : "b@example.com", generation: $"g{i}"),
                Now.AddSeconds(i))!, CancellationToken.None))).ToArray();
        await Task.WhenAll(tasks);

        var state = store.Read()!;
        Assert.Equal("model-10", CursorActivityAttribution.Latest(state, CursorActivityAttribution.AccountKey("a@example.com")!)!.Model);
        Assert.Equal("model-11", CursorActivityAttribution.Latest(state, CursorActivityAttribution.AccountKey("b@example.com")!)!.Model);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("""{"version":2,"entries":[]}""")]
    [InlineData("""{"version":1,"entries":[],"prompt":"x"}""")]
    [InlineData("""{"version":1,"entries":[{"kind":"Request","model":"x","receivedAt":"2026-10-10T03:00:00Z","accountKey":"not-hex"}]}""")]
    public void CorruptOrForeignInboxIsIgnored(string content)
    {
        var store = new CursorActivityStore(_root);
        File.WriteAllText(store.Path, content);

        Assert.Null(store.Read());
    }

    [Fact]
    public async Task MonitorRereadsOnlyWhenTheInboxChanges()
    {
        var monitor = new CursorActivityMonitor(_root);
        Assert.False(monitor.Poll());
        Assert.Null(monitor.Current);

        var store = new CursorActivityStore(_root);
        await store.RecordAsync(Parse(Submit())!, CancellationToken.None);
        Assert.True(monitor.Poll());
        Assert.False(monitor.Poll());
        Assert.Equal("claude-4.5-sonnet", Assert.Single(monitor.Current!.Entries).Model);

        File.Delete(store.Path);
        Assert.True(monitor.Poll());
        Assert.Null(monitor.Current);
    }

    [Fact]
    public void CompletionIsShownOnlyForTheSameGenerationAsTheLatestRequest()
    {
        var key = CursorActivityAttribution.AccountKey("person@example.com")!;
        var state = CursorActivityStore.Merge(null, Parse(Submit(generation: "g1"), Now)!).State;
        state = CursorActivityStore.Merge(state, Parse(Stop(generation: "g1"), Now.AddSeconds(30))!).State;
        Assert.Equal(CursorActivityKind.Completion, CursorActivityAttribution.Latest(state, key)!.Kind);

        // A second session starts a new request; the first session's completion must not hide it.
        state = CursorActivityStore.Merge(state, Parse(Submit(model: "gpt-5", generation: "g2"), Now.AddSeconds(40))!).State;
        var latest = CursorActivityAttribution.Latest(state, key)!;
        Assert.Equal(CursorActivityKind.Request, latest.Kind);
        Assert.Equal("gpt-5", latest.Model);

        // A completion for the parallel first session still does not match the newer request.
        state = CursorActivityStore.Merge(state, Parse(Stop(generation: "g1b"), Now.AddSeconds(50))!).State;
        Assert.Equal(CursorActivityKind.Request, CursorActivityAttribution.Latest(state, key)!.Kind);
    }

    [Fact]
    public void ActivityAttachesOnlyToTheVerifiedMatchingCursorAccountAndChangesNothingElse()
    {
        var state = CursorActivityStore.Merge(null, Parse(Submit(), Now)!).State;
        var matching = CursorAccount("c1", "person@example.com");
        var other = CursorAccount("c2", "other@example.com");
        var codex = new CodexAccountView(new CodexAccountProfile("x1", "", "Codex"), CursorSnapshot(), "person@example.com")
        { IsConnected = true };
        var accounts = new[] { codex, matching, other };

        var attached = CursorActivityAttribution.Attach(accounts, CursorActivityIntegration.Connected, state);

        Assert.Same(codex, attached[0]);
        Assert.Equal("claude-4.5-sonnet", attached[1].CursorActivity!.Latest!.Model);
        Assert.True(attached[2].CursorActivity!.AccountVerified);
        Assert.Null(attached[2].CursorActivity!.Latest);
        Assert.Equal(accounts.Select(a => a.Profile.Id), attached.Select(a => a.Profile.Id));
        Assert.Same(matching.Snapshot, attached[1].Snapshot);
    }

    [Fact]
    public void AccountSwitchShowsEachAccountItsOwnReceipt()
    {
        var state = CursorActivityStore.Merge(null, Parse(Submit(model: "model-a", email: "a@example.com", generation: "1"), Now)!).State;
        state = CursorActivityStore.Merge(state, Parse(Submit(model: "model-b", email: "b@example.com", generation: "2"), Now.AddMinutes(1))!).State;

        var attached = CursorActivityAttribution.Attach(
            [CursorAccount("a", "a@example.com"), CursorAccount("b", "b@example.com")], CursorActivityIntegration.Connected, state);

        Assert.Equal("model-a", attached[0].CursorActivity!.Latest!.Model);
        Assert.Equal("model-b", attached[1].CursorActivity!.Latest!.Model);
    }

    [Fact]
    public void UnverifiedProtectedOrAmbiguousAccountsGetNoReceipt()
    {
        var state = CursorActivityStore.Merge(null, Parse(Submit(), Now)!).State;
        var noEmail = CursorAccount("a", null);
        var mismatch = CursorAccount("b", "person@example.com", detail: "cursor-identity-mismatch");
        var signedOut = CursorAccount("c", "person@example.com", CodexQuotaStatus.SignedOut);

        foreach (var account in new[] { noEmail, mismatch, signedOut })
        {
            var view = CursorActivityAttribution.Attach([account], CursorActivityIntegration.Connected, state)[0].CursorActivity!;
            Assert.False(view.AccountVerified);
            Assert.Null(view.Latest);
        }

        var duplicated = CursorActivityAttribution.Attach(
            [CursorAccount("d", "person@example.com"), CursorAccount("e", "PERSON@example.com")], CursorActivityIntegration.Connected, state);
        Assert.All(duplicated, account => Assert.Null(account.CursorActivity!.Latest));
    }

    [Fact]
    public void OffLeavesAccountsUntouchedAndDisconnectedProfilesAreSkipped()
    {
        var state = CursorActivityStore.Merge(null, Parse(Submit(), Now)!).State;
        var accounts = new[] { CursorAccount("a", "person@example.com") };
        Assert.Same(accounts, CursorActivityAttribution.Attach(accounts, CursorActivityIntegration.Off, state));

        var disconnected = CursorAccount("b", "person@example.com") with { IsConnected = false };
        Assert.Null(CursorActivityAttribution.Attach([disconnected], CursorActivityIntegration.Connected, state)[0].CursorActivity);
    }

    [Theory]
    [InlineData(UiLanguage.English)]
    [InlineData(UiLanguage.Korean)]
    public void RowSaysWhatTheReceiptProvesInEveryState(UiLanguage language)
    {
        UiText.SetLanguage(language);
        var request = new CursorRecentActivity(CursorActivityKind.Request, "claude-4.5-sonnet", null, "high", null, Now.AddMinutes(-3));
        var completion = request with { Kind = CursorActivityKind.Completion };

        Assert.Null(CursorActivityPresentation.Row(null, "cursor-auto", Now));
        Assert.Null(CursorActivityPresentation.Row(new(CursorActivityIntegration.Off, true, request), "cursor-auto", Now));

        var disconnected = CursorActivityPresentation.Row(new(CursorActivityIntegration.Disconnected, true, request), "cursor-auto", Now)!;
        Assert.Equal(UiText.T("Disconnected", "연동 끊김"), disconnected.Value);

        var unverified = CursorActivityPresentation.Row(new(CursorActivityIntegration.Connected, false, null), "cursor-auto", Now)!;
        Assert.Equal(UiText.T("Account not verified yet", "계정 확인 전"), unverified.Value);

        var none = CursorActivityPresentation.Row(new(CursorActivityIntegration.Connected, true, null), "cursor-auto", Now)!;
        Assert.Equal(UiText.T("None yet", "아직 없음"), none.Value);
        Assert.Equal(UiText.T("Last request · this PC", "최근 요청 · 이 PC"), none.Label);

        var shown = CursorActivityPresentation.Row(new(CursorActivityIntegration.Connected, true, request), "cursor-auto", Now)!;
        Assert.Equal(UiText.T("Last request · this PC", "최근 요청 · 이 PC"), shown.Label);
        Assert.Equal("claude-4.5-sonnet", shown.Value);
        Assert.Contains(UiText.T("Effort high", "추론 high"), shown.Detail!, StringComparison.Ordinal);
        Assert.Contains(UiText.T("3m ago", "3분 전"), shown.Detail!, StringComparison.Ordinal);
        Assert.Contains(CursorActivityPresentation.LinkText, shown.Detail!, StringComparison.Ordinal);
        Assert.Contains(CursorUsagePresentation.QuotaDisplayLabel("cursor-auto"), shown.Tooltip!, StringComparison.Ordinal);
        Assert.Contains(UiText.T("not a billing", "과금"), CursorActivityPresentation.Row(
            new(CursorActivityIntegration.Connected, true, completion), "cursor-auto", Now)!.Tooltip!, StringComparison.Ordinal);
        Assert.Equal(UiText.T("Last completed · this PC", "최근 완료 · 이 PC"), CursorActivityPresentation.Row(
            new(CursorActivityIntegration.Connected, true, completion), "cursor-auto", Now)!.Label);
        Assert.False(shown.EmphasizeDanger);
    }

    [Fact]
    public void MissingModelSaysSoAndThinkingIsShownOnlyWhenReported()
    {
        var activity = new CursorRecentActivity(CursorActivityKind.Request, null, null, null, null, Now);
        Assert.Equal("Model not provided", CursorActivityPresentation.ModelText(activity));
        Assert.Null(CursorActivityPresentation.ReasoningText(activity));
        Assert.Equal("Thinking on", CursorActivityPresentation.ReasoningText(activity with { Thinking = "true" }));
        Assert.Null(CursorActivityPresentation.ReasoningText(activity with { Thinking = "false" }));
        Assert.Equal("id", CursorActivityPresentation.ModelText(activity with { Model = "slug", ModelId = "id" }));
    }

    [Fact]
    public void WidgetTooltipCarriesTheLineAndKeepsPeriodsAndSizeInputsUnchanged()
    {
        var account = CursorAccount("a", "person@example.com");
        var plain = WidgetAccountModel.From(account, true, UsagePeriodPreference.Auto, Now);
        var withActivity = WidgetAccountModel.From(account with
        {
            CursorActivity = new(CursorActivityIntegration.Connected, true,
                new CursorRecentActivity(CursorActivityKind.Request, "gpt-5", null, null, null, Now.AddMinutes(-1)))
        }, true, UsagePeriodPreference.Auto, Now);

        Assert.Equal(plain.Periods.Select(p => (p.PeriodLabel, p.RemainingText)),
            withActivity.Periods.Select(p => (p.PeriodLabel, p.RemainingText)));
        Assert.Equal(plain.StatusText, withActivity.StatusText);
        Assert.DoesNotContain("gpt-5", plain.Tooltip, StringComparison.Ordinal);
        Assert.Contains("Last request · this PC: gpt-5", withActivity.Tooltip, StringComparison.Ordinal);
    }

    private static CodexAccountView CursorAccount(string id, string? email,
        CodexQuotaStatus status = CodexQuotaStatus.Available, string? detail = null) =>
        new(new CodexAccountProfile(id, "", "Cursor " + id) { Provider = UsageProviderId.Cursor }, CursorSnapshot(status, detail), email)
        {
            IsConnected = true
        };

    private static CodexQuotaSnapshot CursorSnapshot(CodexQuotaStatus status = CodexQuotaStatus.Available, string? detail = null) =>
        new(status, "pro", Now.AddMinutes(-3), Now.AddMinutes(-3), null, null, null,
            [new CodexQuotaWindow("cursor-auto", 20, null, Now.AddDays(5), CodexWindowKind.Other)
            {
                UsedAmount = 20, LimitAmount = 100, RemainingAmount = 80, Unit = "USD"
            }], detail)
        {
            Provider = UsageProviderId.Cursor
        };
}
