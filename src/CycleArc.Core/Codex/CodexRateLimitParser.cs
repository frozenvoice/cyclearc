using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CycleArc.Codex;

public static class CodexRateLimitParser
{
    public static CodexParseResult Parse(JsonNode? accountResult, JsonNode? rateLimitsResult)
    {
        if (IsSignedOut(accountResult))
        {
            return new CodexParseResult(CodexQuotaStatus.SignedOut, null, null, null, null, [], "signed-out");
        }

        if (rateLimitsResult is null)
        {
            return new CodexParseResult(CodexQuotaStatus.Unavailable, SafePlanType(accountResult), null, null, null, [], "rate-limits-missing");
        }

        if (HasProtocolError(rateLimitsResult))
        {
            return new CodexParseResult(CodexQuotaStatus.ProtocolMismatch, SafePlanType(accountResult), null, null, null, [], "protocol-error");
        }

        var root = UnwrapResult(rateLimitsResult);
        var bucket = SelectBucket(rateLimitsResult);
        if (HasProtocolError(accountResult) && bucket is null)
        {
            return new CodexParseResult(CodexQuotaStatus.ProtocolMismatch, SafePlanType(accountResult), null, null, null, [], "protocol-error");
        }

        if (bucket is null)
        {
            return new CodexParseResult(CodexQuotaStatus.Unavailable, SafePlanType(accountResult), null, null, null, [], "rate-limits-unavailable");
        }

        var windows = ReadWindows(bucket, out var malformedWindow);
        if (malformedWindow)
        {
            return new CodexParseResult(
                CodexQuotaStatus.ProtocolMismatch,
                SafePlanType(accountResult),
                null,
                null,
                null,
                [],
                "rate-limits-malformed-window");
        }

        var ordinary = (root is null ? null : ReadBool(root, "ordinaryUsageAllowed", "ordinary_usage_allowed"))
                       ?? ReadBool(bucket, "ordinaryUsageAllowed", "ordinary_usage_allowed");
        var reached = ReadString(bucket, "rateLimitReachedType", "rate_limit_reached_type");
        var creditContainer = ResetCreditContainer(root) ?? ResetCreditContainer(bucket);
        var credits = ReadResetCredits(creditContainer);
        var plan = SafePlanType(accountResult) ?? SafePlanTypeFromNode(bucket);
        return new CodexParseResult(
            CodexQuotaStatus.Available,
            plan,
            ordinary,
            reached,
            credits,
            windows,
            null,
            ReadCreditExpirations(creditContainer));
    }

    public static JsonNode? SelectBucket(JsonNode? result)
    {
        var root = UnwrapResult(result);
        if (root is not JsonObject obj)
        {
            return null;
        }

        if (TryGet(obj, out var byId, "rateLimitsByLimitId", "rate_limits_by_limit_id")
            && byId is JsonObject map)
        {
            if (TryGet(map, out var codex, "codex", "Codex") && codex is JsonObject)
            {
                return codex;
            }
        }

        if (TryGet(obj, out var top, "rateLimits", "rate_limits") && top is JsonObject)
        {
            return top;
        }

        if (LooksLikeWindowContainer(obj))
        {
            return obj;
        }

        return null;
    }

    public static IReadOnlyList<CodexQuotaWindow> ReadWindows(JsonNode bucket)
        => ReadWindows(bucket, out _);

    private static IReadOnlyList<CodexQuotaWindow> ReadWindows(JsonNode bucket, out bool malformedWindow)
    {
        var windows = new List<CodexQuotaWindow>();
        malformedWindow = false;

        if (bucket is not JsonObject obj)
        {
            malformedWindow = true;
            return windows;
        }

        malformedWindow = !TryAddOptionalWindow(windows, obj, "primary", null)
                          || !TryAddOptionalWindow(windows, obj, "secondary", null);

        if (!malformedWindow
            && obj.TryGetPropertyValue("windows", out var windowsNode)
            && !IsNull(windowsNode))
        {
            if (windowsNode is not JsonArray array)
            {
                malformedWindow = true;
            }
            else
            {
                foreach (var item in array)
                {
                    if (!TryAddPresentWindow(windows, item, null))
                    {
                        malformedWindow = true;
                        break;
                    }
                }
            }
        }

        if (!malformedWindow && windows.Count == 0 && LooksLikeSingleWindow(obj))
        {
            if (!TryAddPresentWindow(windows, obj, ReadString(obj, "limitId", "limit_id")))
            {
                malformedWindow = true;
            }
        }

        return windows;
    }

    public static CodexQuotaWindow? TryReadWindow(JsonNode? node, string? fallbackLimitId)
    {
        if (node is null || node.GetValueKind() == JsonValueKind.Null)
        {
            return null;
        }

        if (node is not JsonObject)
        {
            return null;
        }

        var minutes = ReadPositiveInt(node, "windowDurationMins", "windowDurationMinutes", "window_duration_mins");
        var used = ReadPercent(node, "usedPercent", "used_percent");
        var resetsAt = ReadUnixSeconds(node, "resetsAt", "resets_at");
        var limitId = ReadString(node, "limitId", "limit_id") ?? fallbackLimitId;
        if (minutes is null && used is null && resetsAt is null && limitId is null)
        {
            return null;
        }

        return new CodexQuotaWindow(
            limitId,
            used,
            minutes,
            resetsAt,
            CodexWindowClassifier.FromDurationMinutes(minutes));
    }

    private static bool TryAddOptionalWindow(
        List<CodexQuotaWindow> windows,
        JsonObject bucket,
        string propertyName,
        string? fallbackLimitId)
    {
        if (!bucket.TryGetPropertyValue(propertyName, out var node) || IsNull(node))
        {
            return true;
        }

        return TryAddPresentWindow(windows, node, fallbackLimitId);
    }

    private static bool TryAddPresentWindow(List<CodexQuotaWindow> windows, JsonNode? node, string? fallbackLimitId)
    {
        var window = TryReadWindow(node, fallbackLimitId);
        if (window is null)
        {
            return false;
        }

        windows.Add(window);
        return true;
    }

    private static bool IsNull(JsonNode? node) =>
        node is null || node.GetValueKind() == JsonValueKind.Null;

    public static double? ReadPercent(JsonNode node, params string[] names)
    {
        foreach (var name in names)
        {
            var value = node[name];
            if (value is null || value.GetValueKind() is JsonValueKind.Null)
            {
                continue;
            }

            if (!TryReadDouble(value, out var number) || !double.IsFinite(number))
            {
                return null;
            }

            return Math.Clamp(number, 0, 100);
        }

        return null;
    }

    public static DateTimeOffset? ReadUnixSeconds(JsonNode node, params string[] names)
    {
        foreach (var name in names)
        {
            var value = node[name];
            if (value is null || value.GetValueKind() is JsonValueKind.Null)
            {
                continue;
            }

            if (!TryReadDouble(value, out var number) || !double.IsFinite(number))
            {
                return null;
            }

            if (number is <= 0 or > 4_102_444_800)
            {
                return null;
            }

            try
            {
                return DateTimeOffset.FromUnixTimeSeconds((long)number);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static int? ReadPositiveInt(JsonNode node, params string[] names)
    {
        foreach (var name in names)
        {
            var value = node[name];
            if (value is null)
            {
                continue;
            }

            if (TryReadDouble(value, out var number) && double.IsFinite(number) && number > 0)
            {
                return (int)number;
            }
        }

        return null;
    }

    private static JsonObject? ResetCreditContainer(JsonNode? node) => node is JsonObject obj
        ? (obj["rateLimitResetCredits"] ?? obj["rate_limit_reset_credits"]) as JsonObject : null;

    private static int? ReadResetCredits(JsonObject? credits)
    {
        var count = credits?["availableCount"] ?? credits?["available_count"];
        return count is not null && TryReadDouble(count, out var value) && double.IsFinite(value)
            && value >= 0 && value <= int.MaxValue && value == Math.Truncate(value) ? (int)value : null;
    }

    public static IReadOnlyList<CodexResetCredit> ReadRedeemableCredits(JsonNode? result)
    {
        var container = ResetCreditContainer(UnwrapResult(result)) ?? ResetCreditContainer(SelectBucket(result));
        if (container?["credits"] is not JsonArray credits) return [];
        var items = new List<CodexResetCredit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in credits)
        {
            if (node is not JsonObject credit) return [];
            var id = ReadString(credit, "id");
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id)) return [];
            if (ReadString(credit, "status") == "available"
                && ReadString(credit, "resetType", "reset_type") == "codexRateLimits")
                items.Add(new(id, ReadUnixSeconds(credit, "expiresAt", "expires_at")));
        }
        return items;
    }

    private static IReadOnlyList<DateTimeOffset?>? ReadCreditExpirations(JsonObject? container)
    {
        if (container?["credits"] is not JsonArray credits) return null;
        var dates = new List<DateTimeOffset?>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var credit in credits.OfType<JsonObject>())
        {
            // IDs are used only to deduplicate this response, never retained in the cache.
            var id = ReadString(credit, "id");
            if (id is null || !seen.Add(id)
                || ReadString(credit, "status") != "available"
                || ReadString(credit, "resetType", "reset_type") != "codexRateLimits") continue;
            dates.Add(ReadUnixSeconds(credit, "expiresAt", "expires_at"));
        }
        return dates;
    }

    private static bool? ReadBool(JsonNode node, params string[] names)
    {
        foreach (var name in names)
        {
            var value = node[name];
            if (value is null)
            {
                continue;
            }

            if (value.GetValueKind() == JsonValueKind.True)
            {
                return true;
            }

            if (value.GetValueKind() == JsonValueKind.False)
            {
                return false;
            }

            if (bool.TryParse(value.ToString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static string? ReadString(JsonNode node, params string[] names)
    {
        foreach (var name in names)
        {
            var value = node[name];
            if (value is null || value.GetValueKind() is JsonValueKind.Null)
            {
                continue;
            }

            var text = value.GetValueKind() == JsonValueKind.String
                ? value.GetValue<string>()
                : value.ToString();
            if (!string.IsNullOrWhiteSpace(text) && text != "null")
            {
                return text;
            }
        }

        return null;
    }

    private static bool TryReadDouble(JsonNode value, out double number)
    {
        number = 0;
        try
        {
            number = value.GetValue<double>();
            return true;
        }
        catch
        {
            return double.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out number);
        }
    }

    private static bool TryGet(JsonObject obj, out JsonNode? value, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj.TryGetPropertyValue(name, out value) && value is not null && value.GetValueKind() != JsonValueKind.Null)
            {
                return true;
            }
        }

        value = null;
        return false;
    }

    private static JsonNode? UnwrapResult(JsonNode? node)
    {
        if (node is JsonObject obj && obj.TryGetPropertyValue("result", out var result) && result is not null)
        {
            return result;
        }

        return node;
    }

    private static bool LooksLikeWindowContainer(JsonObject obj) =>
        obj.ContainsKey("primary")
        || obj.ContainsKey("secondary")
        || obj.ContainsKey("windows")
        || obj.ContainsKey("usedPercent")
        || obj.ContainsKey("windowDurationMins");

    private static bool LooksLikeSingleWindow(JsonNode bucket) =>
        bucket["usedPercent"] is not null || bucket["windowDurationMins"] is not null;

    private static bool HasProtocolError(JsonNode? node) =>
        node is JsonObject obj && obj.TryGetPropertyValue("error", out var error) && error is not null;

    private static bool IsSignedOut(JsonNode? accountResult)
    {
        var root = UnwrapResult(accountResult);
        if (root is null)
        {
            return false;
        }

        if (root["loggedIn"] is { } loggedIn)
        {
            if (loggedIn.GetValueKind() == JsonValueKind.False)
            {
                return true;
            }

            if (bool.TryParse(loggedIn.ToString(), out var parsed) && !parsed)
            {
                return true;
            }
        }

        if (root["signedIn"] is { } signedIn
            && (signedIn.GetValueKind() == JsonValueKind.False
                || (bool.TryParse(signedIn.ToString(), out var signed) && !signed)))
        {
            return true;
        }

        if (HasProtocolError(accountResult) && LooksLikeAuthError(accountResult?["error"]))
        {
            return true;
        }

        return false;
    }

    private static bool LooksLikeAuthError(JsonNode? error)
    {
        var text = error?.ToString() ?? "";
        return text.Contains("signed out", StringComparison.OrdinalIgnoreCase)
               || text.Contains("not logged", StringComparison.OrdinalIgnoreCase)
               || text.Contains("unauthenticated", StringComparison.OrdinalIgnoreCase)
               || text.Contains("ACCOUNT_NOT_FOUND", StringComparison.OrdinalIgnoreCase);
    }

    private static string? SafePlanType(JsonNode? accountResult)
    {
        var root = UnwrapResult(accountResult);
        var account = root?["account"] ?? root;
        return SafePlanTypeFromNode(account);
    }

    private static string? SafePlanTypeFromNode(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        var plan = ReadString(node, "planType", "plan_type", "plan");
        if (string.IsNullOrWhiteSpace(plan)
            || plan.Contains('@', StringComparison.Ordinal)
            || plan.Contains("token", StringComparison.OrdinalIgnoreCase)
            || plan.Contains("cookie", StringComparison.OrdinalIgnoreCase)
            || plan.Contains("accountId", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return plan;
    }
}

public sealed record CodexParseResult(
    CodexQuotaStatus Status,
    string? PlanType,
    bool? OrdinaryUsageAllowed,
    string? RateLimitReachedType,
    int? ResetCreditsAvailable,
    IReadOnlyList<CodexQuotaWindow> Windows,
    string? Detail,
    IReadOnlyList<DateTimeOffset?>? ResetCreditExpirations = null);
