using System.Globalization;
using System.Text.Json;

namespace CycleArc.Providers.Claude;

/// <summary>Read-only projected inventory. No grant identifiers, redemption requests or credentials.</summary>
public sealed record ClaudeResetCreditGrant(int ResetsLeft, int ResetsTotal,
    DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, bool Paused,
    bool? UsableNow, bool? UseRequiresLimit, IReadOnlyList<string> Clears)
{
    public bool AvailableAt(DateTimeOffset now) => ResetsLeft > 0 && !Paused
        && (StartsAt is null || StartsAt <= now) && (EndsAt is null || EndsAt > now);
}

public sealed record ClaudeResetCreditInventory(bool Eligible,
    IReadOnlyList<ClaudeResetCreditGrant> Grants, DateTimeOffset ObservedAt)
{
    public int AvailableCount(DateTimeOffset now) => Eligible
        ? Grants.Where(grant => grant.AvailableAt(now)).Sum(grant => grant.ResetsLeft) : 0;
    // A missing usable_now is unknown, rather than a promise that redemption works.
    public int? UsableCount(DateTimeOffset now) => !Eligible ? 0
        : Grants.Any(grant => grant.AvailableAt(now) && grant.UsableNow is null) ? null
        : Grants.Where(grant => grant.AvailableAt(now) && grant.UsableNow == true).Sum(grant => grant.ResetsLeft);
    public DateTimeOffset? NextExpiration(DateTimeOffset now) => !Eligible ? null
        : Grants.Where(grant => grant.AvailableAt(now) && grant.EndsAt is not null)
            .Select(grant => grant.EndsAt).OrderBy(end => end).FirstOrDefault();

    public static bool Valid(ClaudeResetCreditInventory? value, DateTimeOffset now) => value is null
        || (value.ObservedAt > DateTimeOffset.UnixEpoch && value.ObservedAt <= now
            && value.Grants is { Count: <= 200 } && value.Grants.All(grant => grant is not null
                && grant.ResetsLeft is >= 0 and <= 50 && grant.ResetsTotal is >= 0 and <= 50
                && grant.ResetsLeft <= grant.ResetsTotal
                && (grant.StartsAt is null || grant.StartsAt > DateTimeOffset.UnixEpoch)
                && (grant.EndsAt is null || grant.EndsAt > DateTimeOffset.UnixEpoch)
                && (grant.StartsAt is null || grant.EndsAt is null || grant.StartsAt < grant.EndsAt)
                && grant.Clears is { Count: <= 2 } && grant.Clears.All(clear => clear is "five_hour" or "seven_day"))
            && value.Grants.Sum(grant => grant.ResetsLeft) <= 50);

    public static ClaudeResetCreditInventory? Parse(JsonElement root, DateTimeOffset observedAt)
    {
        if (!root.TryGetProperty("cedar_ember", out var program) || program.ValueKind == JsonValueKind.Null) return null;
        Object(program);
        var eligible = Boolean(program, "eligible", required: true)!.Value;
        // Ineligibility (including a surface gate) is reported, never converted into a known empty wallet.
        if (!eligible) return new(false, [], observedAt);
        if (!program.TryGetProperty("grants", out var grants) || grants.ValueKind != JsonValueKind.Array
            || grants.GetArrayLength() > 200) throw new InvalidDataException("Invalid Claude reset inventory.");
        var values = new List<ClaudeResetCreditGrant>();
        foreach (var grant in grants.EnumerateArray())
        {
            Object(grant);
            var left = Count(grant, "resets_left");
            var total = Count(grant, "resets_total");
            var starts = Date(grant, "starts_at");
            var ends = Date(grant, "ends_at");
            var paused = Boolean(grant, "paused", required: true)!.Value;
            var clears = new List<string>();
            if (grant.TryGetProperty("clears", out var targets) && targets.ValueKind != JsonValueKind.Null)
            {
                if (targets.ValueKind != JsonValueKind.Array || targets.GetArrayLength() > 16)
                    throw new InvalidDataException("Invalid Claude reset targets.");
                foreach (var target in targets.EnumerateArray())
                {
                    if (target.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid Claude reset target.");
                    if (target.GetString() is "five_hour" or "seven_day" && !clears.Contains(target.GetString()!))
                        clears.Add(target.GetString()!);
                }
            }
            values.Add(new(left, total, starts, ends, paused, Boolean(grant, "usable_now"),
                Boolean(grant, "use_requires_limit"), clears));
        }
        var result = new ClaudeResetCreditInventory(true, values, observedAt);
        if (!Valid(result, observedAt)) throw new InvalidDataException("Invalid Claude reset inventory.");
        return result;
    }

    private static void Object(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() > 1))
            throw new InvalidDataException("Invalid Claude reset object.");
    }
    private static int Count(JsonElement value, string name) => value.TryGetProperty(name, out var count)
        && count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var result) && result is >= 0 and <= 50
        ? result : throw new InvalidDataException("Invalid Claude reset count.");
    private static bool? Boolean(JsonElement value, string name, bool required = false)
    {
        if (!value.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null)
            return required ? throw new InvalidDataException("Missing Claude reset state.") : null;
        return field.ValueKind is JsonValueKind.True or JsonValueKind.False ? field.GetBoolean()
            : throw new InvalidDataException("Invalid Claude reset state.");
    }
    private static DateTimeOffset? Date(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var field)) throw new InvalidDataException("Missing Claude reset date.");
        if (field.ValueKind == JsonValueKind.Null) return null;
        if (field.ValueKind != JsonValueKind.String || field.GetString() is not { } raw || raw.Length > 80
            || !DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result)
            || result <= DateTimeOffset.UnixEpoch || (!raw.EndsWith('Z')
                && !System.Text.RegularExpressions.Regex.IsMatch(raw, @"[+-]\d{2}:\d{2}$")))
            throw new InvalidDataException("Invalid Claude reset date.");
        return result;
    }
}
