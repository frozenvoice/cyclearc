using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CycleArc.Codex;
using CycleArc.Services;

namespace CycleArc.Providers.Cursor;

/// <summary>
/// What a Cursor hook receipt proves. <c>beforeSubmitPrompt</c> runs after the user sends a
/// prompt and before Cursor's backend request; <c>stop</c> with <c>status: "completed"</c>
/// runs when the agent loop ends. Neither is a billing record.
/// </summary>
public enum CursorActivityKind
{
    Request,
    Completion
}

/// <summary>
/// One hook receipt reduced to what CycleArc may keep: the model fields Cursor supplied, a
/// hashed account key, a hashed generation for de-duplication and the local receipt time.
/// Prompt text, attachments, transcripts, workspace paths and the raw e-mail are never kept.
/// </summary>
public sealed record CursorActivityEntry(
    CursorActivityKind Kind,
    string? Model,
    string? ModelId,
    string? Effort,
    string? Thinking,
    string? AccountKey,
    string? Generation,
    DateTimeOffset ReceivedAt);

public sealed record CursorActivityState(int Version, IReadOnlyList<CursorActivityEntry> Entries)
{
    public bool SameAs(CursorActivityState? other) => other is not null && Version == other.Version
        && Entries.SequenceEqual(other.Entries);
}

/// <summary>Parses one official Cursor hook input without materializing unrelated fields.</summary>
public static class CursorHookEvent
{
    public const string SubmitEvent = "beforeSubmitPrompt";
    public const string StopEvent = "stop";
    internal const int MaxModelLength = 128;
    internal const int MaxParameterLength = 32;

    public static CursorActivityEntry? Parse(ReadOnlySpan<byte> json, DateTimeOffset receivedAt)
    {
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 64 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            string? hook = null, model = null, modelId = null, email = null, generation = null, status = null;
            string? effort = null, thinking = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) return null;
                var name = reader.GetString()!;
                if (!reader.Read()) return null;
                switch (name)
                {
                    case "hook_event_name" or "model" or "model_id" or "user_email" or "generation_id" or "status":
                        // A repeated field would let two values compete; treat the input as malformed.
                        if (!seen.Add(name)) return null;
                        if (reader.TokenType == JsonTokenType.Null) break;
                        if (reader.TokenType != JsonTokenType.String) return null;
                        var text = reader.GetString();
                        if (name == "hook_event_name") hook = text;
                        else if (name == "model") model = text;
                        else if (name == "model_id") modelId = text;
                        else if (name == "user_email") email = text;
                        else if (name == "generation_id") generation = text;
                        else status = text;
                        break;
                    case "model_params":
                        if (!seen.Add(name)) return null;
                        if (reader.TokenType == JsonTokenType.Null) break;
                        if (reader.TokenType != JsonTokenType.StartArray) return null;
                        (effort, thinking) = ReadParameters(ref reader);
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }

            var kind = hook switch
            {
                SubmitEvent => CursorActivityKind.Request,
                // Only a completed loop is a completion. Aborted or failed loops are not shown.
                StopEvent when status == "completed" => CursorActivityKind.Completion,
                _ => (CursorActivityKind?)null
            };
            if (kind is null) return null;
            return new CursorActivityEntry(kind.Value, Safe(model, MaxModelLength), Safe(modelId, MaxModelLength),
                Safe(effort, MaxParameterLength), Safe(thinking, MaxParameterLength),
                CursorActivityAttribution.AccountKey(email), GenerationKey(generation), receivedAt);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    // model_params is [{ "id": "...", "value": "..." }]. Only the documented reasoning
    // settings are kept; context size and any future parameter are ignored.
    private static (string? Effort, string? Thinking) ReadParameters(ref Utf8JsonReader reader)
    {
        string? effort = null, thinking = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); continue; }
            string? id = null, value = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var name = reader.GetString();
                reader.Read();
                if (name is "id" or "value" && reader.TokenType == JsonTokenType.String)
                {
                    if (name == "id") id = reader.GetString();
                    else value = reader.GetString();
                }
                else reader.Skip();
            }
            if (id == "effort") effort ??= value;
            else if (id == "thinking") thinking ??= value;
        }
        return (effort, thinking);
    }

    internal static string? Safe(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Length > maxLength) return null;
        foreach (var c in trimmed)
            if (char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format) return null;
        return trimmed;
    }

    private static string? GenerationKey(string? generation)
    {
        if (string.IsNullOrWhiteSpace(generation) || generation.Length > 256 || generation.Any(char.IsControl)) return null;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(generation)))[..32];
    }
}

/// <summary>
/// The local inbox for Cursor hook receipts. Concurrent hook processes serialize through a
/// bounded lock and replace the file atomically. Each account keeps at most its latest request
/// and its latest completion, so the file stays small and holds no history.
/// </summary>
public sealed class CursorActivityStore
{
    public const string FileName = "cursor-activity.json";
    public const int CurrentVersion = 1;
    public const int MaxAccounts = 8;
    private const int MaxFileBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
        MaxDepth = 8
    };
    private readonly string _path;

    public CursorActivityStore(string rootDirectory) =>
        _path = System.IO.Path.Combine(System.IO.Path.GetFullPath(rootDirectory), FileName);

    public string Path => _path;

    public CursorActivityState? Read()
    {
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length is 0 or > MaxFileBytes) return null;
            var state = JsonSerializer.Deserialize<CursorActivityState>(stream, Options);
            return state is not null && Valid(state) ? state : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Merges one receipt. Returns false when it was a duplicate or older than the stored one.</summary>
    public async Task<bool> RecordAsync(CursorActivityEntry entry, CancellationToken token)
    {
        if (!ValidEntry(entry)) return false;
        using var lease = await AcquireAsync(token).ConfigureAwait(false);
        var (state, changed) = Merge(Read(), entry);
        if (!changed) return false;
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, state, Options, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temp, _path, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        return true;
    }

    /// <summary>
    /// A receipt replaces the same account's same kind only when it was received later and is
    /// not a repeat of the stored generation. A slow hook process therefore cannot overwrite a
    /// newer receipt, and a duplicated event is ignored.
    /// </summary>
    public static (CursorActivityState State, bool Changed) Merge(CursorActivityState? current, CursorActivityEntry entry)
    {
        var entries = (current?.Entries ?? []).ToList();
        var index = entries.FindIndex(existing => existing.Kind == entry.Kind
            && string.Equals(existing.AccountKey, entry.AccountKey, StringComparison.Ordinal));
        if (index >= 0)
        {
            var existing = entries[index];
            if (entry.Generation is not null && entry.Generation == existing.Generation
                || entry.ReceivedAt <= existing.ReceivedAt)
                return (current!, false);
            entries[index] = entry;
        }
        else entries.Add(entry);

        var keep = entries.GroupBy(item => item.AccountKey ?? "", StringComparer.Ordinal)
            .OrderByDescending(group => group.Max(item => item.ReceivedAt))
            .Take(MaxAccounts)
            .SelectMany(group => group)
            .OrderBy(item => item.AccountKey ?? "", StringComparer.Ordinal)
            .ThenBy(item => item.Kind)
            .ToArray();
        return (new CursorActivityState(CurrentVersion, keep), true);
    }

    private static bool Valid(CursorActivityState state) =>
        state.Version == CurrentVersion && state.Entries is { Count: <= MaxAccounts * 2 }
        && state.Entries.All(ValidEntry)
        && state.Entries.Select(entry => (entry.AccountKey ?? "", entry.Kind)).Distinct().Count() == state.Entries.Count;

    private static bool ValidEntry(CursorActivityEntry entry) =>
        Enum.IsDefined(entry.Kind)
        && entry.ReceivedAt > DateTimeOffset.UnixEpoch
        && Same(entry.Model, CursorHookEvent.MaxModelLength) && Same(entry.ModelId, CursorHookEvent.MaxModelLength)
        && Same(entry.Effort, CursorHookEvent.MaxParameterLength) && Same(entry.Thinking, CursorHookEvent.MaxParameterLength)
        && (entry.AccountKey is null || IsHex(entry.AccountKey, 64))
        && (entry.Generation is null || IsHex(entry.Generation, 32));

    private static bool Same(string? value, int maxLength) => value is null || CursorHookEvent.Safe(value, maxLength) == value;

    private static bool IsHex(string value, int length) => value.Length == length
        && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    private async Task<FileStream> AcquireAsync(CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (watch.Elapsed < TimeSpan.FromSeconds(1))
            {
                await Task.Delay(25, token).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>
/// Desktop-side change detection for the inbox. A receipt only changes what the popup and
/// widget say about recent activity; it never starts a usage request.
/// </summary>
public sealed class CursorActivityMonitor
{
    private readonly CursorActivityStore _store;
    private (DateTime Written, long Length)? _stamp;

    public CursorActivityMonitor(string rootDirectory) => _store = new CursorActivityStore(rootDirectory);

    public CursorActivityState? Current { get; private set; }

    /// <summary>Rereads the inbox only when its file changed. Returns true when the content changed.</summary>
    public bool Poll()
    {
        (DateTime, long)? stamp = null;
        try
        {
            var info = new FileInfo(_store.Path);
            if (info.Exists) stamp = (info.LastWriteTimeUtc, info.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        if (stamp == _stamp && _stamp is not null) return false;
        _stamp = stamp;
        var next = stamp is null ? null : _store.Read();
        if (next is null ? Current is null : next.SameAs(Current)) return false;
        Current = next;
        return true;
    }
}

/// <summary>Whether the user enabled Cursor activity and whether its hook entries are present.</summary>
public enum CursorActivityIntegration
{
    Off,
    Connected,
    Disconnected
}

public sealed record CursorRecentActivity(CursorActivityKind Kind, string? Model, string? ModelId,
    string? Effort, string? Thinking, DateTimeOffset ReceivedAt);

/// <summary>Per-account projection: in memory only, never persisted with the account or its quota.</summary>
public sealed record CursorActivityView(CursorActivityIntegration Integration, bool AccountVerified,
    CursorRecentActivity? Latest);

public static class CursorActivityAttribution
{
    public static string? AccountKey(string? email) => CursorIdentity.Normalize(email) is { } normalized
        ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
        : null;

    /// <summary>
    /// The latest request for the account, shown as completed only when the latest completion
    /// belongs to that same generation. A completion from an older or parallel session never
    /// hides a newer request.
    /// </summary>
    public static CursorRecentActivity? Latest(CursorActivityState? state, string accountKey)
    {
        if (state is null) return null;
        var request = state.Entries.FirstOrDefault(entry => entry.Kind == CursorActivityKind.Request
            && entry.AccountKey == accountKey);
        var completion = state.Entries.FirstOrDefault(entry => entry.Kind == CursorActivityKind.Completion
            && entry.AccountKey == accountKey);
        var shown = request is null ? completion
            : completion is not null && completion.Generation is not null
                && completion.Generation == request.Generation && completion.ReceivedAt >= request.ReceivedAt
                ? completion : request;
        return shown is null ? null
            : new CursorRecentActivity(shown.Kind, shown.Model, shown.ModelId, shown.Effort, shown.Thinking, shown.ReceivedAt);
    }

    /// <summary>
    /// Attaches recent activity to connected Cursor accounts whose server-verified e-mail matches
    /// the hook's <c>user_email</c>. Selection, order, snapshots and every other provider stay as
    /// they are. An account whose identity is protected, or that shares a key with another
    /// connected account, gets no activity rather than a guessed one.
    /// </summary>
    public static IReadOnlyList<CodexAccountView> Attach(IReadOnlyList<CodexAccountView> accounts,
        CursorActivityIntegration integration, CursorActivityState? state)
    {
        if (integration == CursorActivityIntegration.Off) return accounts;
        var keys = accounts.Where(Eligible).Select(account => AccountKey(account.Email))
            .Where(key => key is not null).GroupBy(key => key!).Where(group => group.Count() == 1)
            .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var result = new CodexAccountView[accounts.Count];
        for (var i = 0; i < accounts.Count; i++)
        {
            var account = accounts[i];
            if (account.Profile.Provider != Usage.UsageProviderId.Cursor || !account.IsConnected)
            {
                result[i] = account;
                continue;
            }
            var key = Eligible(account) ? AccountKey(account.Email) : null;
            var verified = key is not null && keys.Contains(key);
            result[i] = account with
            {
                CursorActivity = new CursorActivityView(integration, verified, verified ? Latest(state, key!) : null)
            };
        }
        return result;
    }

    private static bool Eligible(CodexAccountView account) =>
        account.Profile.Provider == Usage.UsageProviderId.Cursor && account.IsConnected
        && account.Snapshot.Status != CodexQuotaStatus.SignedOut
        && account.Snapshot.TechnicalDetail is not ("cursor-identity-mismatch" or "cursor-live-identity-mismatch")
        && !CodexIdentityPresentation.NeedsReconnection(account.Snapshot);
}

/// <summary>Wording for recent Cursor activity. Says only what the hook receipt proves.</summary>
public static class CursorActivityPresentation
{
    public static string Label(CursorActivityView view) => view.Latest?.Kind == CursorActivityKind.Completion
        ? UiText.T("Last completed · this PC", "최근 완료 · 이 PC")
        : UiText.T("Last request · this PC", "최근 요청 · 이 PC");

    /// <summary>The model Cursor reported: the structured id when supplied, otherwise its legacy slug.</summary>
    public static string ModelText(CursorRecentActivity activity) =>
        activity.ModelId ?? activity.Model ?? UiText.T("Model not provided", "모델 정보 없음");

    public static string? ReasoningText(CursorRecentActivity activity)
    {
        if (activity.Effort is { } effort) return UiText.T($"Effort {effort}", $"추론 {effort}");
        if (string.Equals(activity.Thinking, "true", StringComparison.OrdinalIgnoreCase))
            return UiText.T("Thinking on", "Thinking 켜짐");
        return null;
    }

    public static string LinkText => UiText.T("Limit link unconfirmed", "한도 연결 미확인");

    /// <summary>One popup row, or null when the integration is off.</summary>
    public static CodexDisplayRow? Row(CursorActivityView? view, string? ringLimitId, DateTimeOffset now)
    {
        if (view is null || view.Integration == CursorActivityIntegration.Off) return null;
        var label = Label(view);
        if (view.Integration == CursorActivityIntegration.Disconnected)
            return new(label, UiText.T("Disconnected", "연동 끊김"), false,
                UiText.T("Reconnect in Settings", "설정에서 다시 연결하세요"), DisconnectedTooltip);
        if (!view.AccountVerified)
            return new(label, UiText.T("Account not verified yet", "계정 확인 전"), false,
                UiText.T("Shown after this account's Cursor usage is checked", "이 계정의 Cursor 사용량을 확인한 뒤 표시합니다"),
                SourceTooltip(ringLimitId));
        if (view.Latest is not { } latest)
            return new(label, UiText.T("None yet", "아직 없음"), false,
                UiText.T("No request received on this PC", "이 PC에서 받은 요청이 없습니다"), SourceTooltip(ringLimitId));
        var detail = string.Join(" · ", new[]
        {
            ReasoningText(latest),
            CodexDeadlineFormatting.Elapsed(latest.ReceivedAt, now),
            LinkText
        }.Where(part => !string.IsNullOrEmpty(part)));
        return new(label, ModelText(latest), false, detail, Tooltip(latest, ringLimitId));
    }

    /// <summary>A single tooltip line for the widget; empty when nothing should be said.</summary>
    public static string? TooltipLine(CursorActivityView? view, DateTimeOffset now)
    {
        if (view is null || view.Integration == CursorActivityIntegration.Off) return null;
        if (view.Integration == CursorActivityIntegration.Disconnected)
            return Label(view) + ": " + UiText.T("disconnected", "연동 끊김");
        if (!view.AccountVerified)
            return Label(view) + ": " + UiText.T("account not verified yet", "계정 확인 전");
        if (view.Latest is not { } latest)
            return Label(view) + ": " + UiText.T("none yet", "아직 없음");
        return Label(view) + ": " + string.Join(" · ", new[]
        {
            ModelText(latest),
            ReasoningText(latest),
            CodexDeadlineFormatting.Elapsed(latest.ReceivedAt, now),
            LinkText
        }.Where(part => !string.IsNullOrEmpty(part)));
    }

    private static string RingText(string? ringLimitId) => ringLimitId is null
        ? UiText.T("The ring has no limit to show.", "링에 표시할 한도가 없습니다.")
        : UiText.T($"The ring keeps showing {CursorUsagePresentation.QuotaDisplayLabel(ringLimitId)}, the representative limit.",
            $"링은 대표 한도인 {CursorUsagePresentation.QuotaDisplayLabel(ringLimitId)}를 계속 표시합니다.");

    private static string SourceTooltip(string? ringLimitId) => string.Join(Environment.NewLine,
        UiText.T("Received from Cursor hooks on this PC only. Use on other PCs or the web is not included.",
            "이 PC의 Cursor 훅에서 받은 정보만 표시합니다. 다른 PC나 웹에서의 사용은 포함되지 않습니다."),
        UiText.T("Which limit a model draws from is not confirmed.", "모델이 어느 한도에서 차감되는지는 확인되지 않았습니다."),
        RingText(ringLimitId));

    private static string DisconnectedTooltip => UiText.T(
        "CycleArc's entries are no longer in Cursor's hooks.json. Remaining usage is unaffected. Turn the option off and on again in Settings to reconnect.",
        "Cursor hooks.json에서 CycleArc 항목을 찾을 수 없습니다. 잔여량 조회에는 영향이 없습니다. 설정에서 옵션을 껐다 켜면 다시 연결합니다.");

    private static string Tooltip(CursorRecentActivity latest, string? ringLimitId)
    {
        var meaning = latest.Kind == CursorActivityKind.Completion
            ? UiText.T("Cursor reported that this request's agent run completed. This is not a billing record.",
                "Cursor가 이 요청의 에이전트 실행 완료를 알렸습니다. 과금 기록이 아닙니다.")
            : UiText.T("Cursor reported this request when it was sent, before its response. It may not have completed or been billed.",
                "Cursor가 요청을 보낼 때(응답 전) 알린 정보입니다. 완료되거나 과금되었다는 뜻은 아닙니다.");
        var model = UiText.T("The model is the one selected in Cursor's composer; a model chosen inside Auto is not included.",
            "모델은 Cursor 작성기에서 선택된 모델입니다. Auto 안에서 실제로 선택된 모델은 포함되지 않습니다.");
        var at = UiText.T($"Received {latest.ReceivedAt.ToLocalTime():yyyy-MM-dd HH:mm}",
            $"수신 {latest.ReceivedAt.ToLocalTime():yyyy-MM-dd HH:mm}");
        return string.Join(Environment.NewLine, meaning, model, at, SourceTooltip(ringLimitId));
    }
}
