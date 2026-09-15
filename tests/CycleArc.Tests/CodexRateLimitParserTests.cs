using CycleArc.Codex;

namespace CycleArc.Tests;

public class CodexRateLimitParserTests
{
    [Fact]
    public void OfficialGetAccountRateLimitsResponse_ReadsRootMetadataAndSelectedSnapshot()
    {
        var parsed = CodexRateLimitParser.Parse(
            null,
            JsonNode.Parse("""
            {
              "result": {
                "ordinaryUsageAllowed": true,
                "rateLimits": {
                  "limitId": "codex",
                  "primary": {
                    "usedPercent": 42,
                    "windowDurationMins": 300,
                    "resetsAt": 1893456000
                  },
                  "secondary": {
                    "usedPercent": 31,
                    "windowDurationMins": 10080,
                    "resetsAt": 1894051200
                  },
                  "planType": "pro",
                  "rateLimitReachedType": null
                },
                "rateLimitsByLimitId": {
                  "codex": {
                    "limitId": "codex",
                    "primary": {
                      "usedPercent": 42,
                      "windowDurationMins": 300,
                      "resetsAt": 1893456000
                    },
                    "secondary": {
                      "usedPercent": 31,
                      "windowDurationMins": 10080,
                      "resetsAt": 1894051200
                    },
                    "planType": "pro",
                    "rateLimitReachedType": null
                  }
                },
                "rateLimitResetCredits": {
                  "availableCount": 1,
                  "credits": null
                },
                "accountId": "must-not-be-persisted"
              }
            }
            """));
        Assert.Equal(CodexQuotaStatus.Available, parsed.Status);
        Assert.Equal(42, parsed.Windows[0].UsedPercent);
        Assert.Equal(CodexWindowKind.FiveHour, parsed.Windows[0].Kind);
        Assert.Equal(31, parsed.Windows[1].UsedPercent);
        Assert.Equal(CodexWindowKind.Weekly, parsed.Windows[1].Kind);
        Assert.True(parsed.OrdinaryUsageAllowed);
        Assert.Equal(1, parsed.ResetCreditsAvailable);
        Assert.Equal("pro", parsed.PlanType);
        Assert.Null(parsed.RateLimitReachedType);
        Assert.DoesNotContain("must-not-be-persisted", parsed.Detail ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("accountId", parsed.Detail ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "must-not-be-persisted",
            CodexProtocol.SanitizeDiagnostic("""{"accountId":"must-not-be-persisted"}"""),
            StringComparison.Ordinal);

        var path = Path.Combine(Path.GetTempPath(), $"cyclearc-codex-official-{Guid.NewGuid():N}.json");
        var store = new CodexSnapshotStore(path);
        store.Save(new CodexQuotaSnapshot(
            parsed.Status,
            parsed.PlanType,
            DateTimeOffset.Parse("2026-09-05T02:00:00Z"),
            DateTimeOffset.Parse("2026-09-05T02:00:00Z"),
            parsed.OrdinaryUsageAllowed,
            parsed.RateLimitReachedType,
            parsed.ResetCreditsAvailable,
            parsed.Windows,
            parsed.Detail));
        var json = File.ReadAllText(path);
        Assert.DoesNotContain("accountId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("must-not-be-persisted", json, StringComparison.Ordinal);
        Assert.Equal("pro", store.Load()?.PlanType);
        File.Delete(path);
    }

    [Fact]
    public void NestedBucketMetadata_RemainsBackwardCompatible()
    {
        var parsed = CodexRateLimitParser.Parse(null, JsonNode.Parse("""
            {
              "rateLimits": {
                "primary": { "usedPercent": 42, "windowDurationMins": 300, "resetsAt": 1893456000, "limitId": "codex" },
                "secondary": { "usedPercent": 31, "windowDurationMins": 10080, "resetsAt": 1894051200 },
                "ordinaryUsageAllowed": true,
                "rateLimitReachedType": "none",
                "rateLimitResetCredits": { "availableCount": 1 }
              }
            }
            """));
        Assert.True(parsed.OrdinaryUsageAllowed);
        Assert.Equal(1, parsed.ResetCreditsAvailable);
        Assert.Equal("none", parsed.RateLimitReachedType);
    }

    [Fact]
    public void SnapshotPlanType_FallsBackFromSelectedRateLimitBucket()
    {
        var parsed = CodexRateLimitParser.Parse(null, JsonNode.Parse("""
            {
              "ordinaryUsageAllowed": false,
              "rateLimitsByLimitId": {
                "codex": {
                  "primary": { "usedPercent": 8, "windowDurationMins": 300 },
                  "planType": "pro"
                }
              },
              "rateLimitResetCredits": { "availableCount": 2 }
            }
            """));
        Assert.Equal("pro", parsed.PlanType);
        Assert.False(parsed.OrdinaryUsageAllowed);
        Assert.Equal(2, parsed.ResetCreditsAvailable);
    }

    [Fact]
    public void RateLimitsByLimitId_PrefersCodexBucket()
    {
        var parsed = CodexRateLimitParser.Parse(null, JsonNode.Parse("""
            {
              "rateLimits": {
                "primary": { "usedPercent": 9, "windowDurationMins": 300 }
              },
              "rateLimitsByLimitId": {
                "chatgpt": { "primary": { "usedPercent": 80, "windowDurationMins": 300 } },
                "codex": { "primary": { "usedPercent": 12, "windowDurationMins": 300 } }
              }
            }
            """));
        Assert.Single(parsed.Windows);
        Assert.Equal(12, parsed.Windows[0].UsedPercent);
    }

    [Fact]
    public void PrimaryWeeklyWithNullSecondary_IsNotFailure()
    {
        var parsed = CodexRateLimitParser.Parse(null, JsonNode.Parse("""
            {
              "rateLimits": {
                "primary": { "usedPercent": 20, "windowDurationMins": 10080 },
                "secondary": null
              }
            }
            """));
        Assert.Equal(CodexQuotaStatus.Available, parsed.Status);
        Assert.Single(parsed.Windows);
        Assert.Equal(CodexWindowKind.Weekly, parsed.Windows[0].Kind);
    }

    [Fact]
    public void ReversedSlots_StillClassifyByDuration()
    {
        var parsed = CodexRateLimitParser.Parse(null, JsonNode.Parse("""
            {
              "rateLimits": {
                "primary": { "usedPercent": 10, "windowDurationMins": 10080 },
                "secondary": { "usedPercent": 40, "windowDurationMins": 300 }
              }
            }
            """));
        Assert.Equal(CodexWindowKind.Weekly, parsed.Windows[0].Kind);
        Assert.Equal(CodexWindowKind.FiveHour, parsed.Windows[1].Kind);
    }

    [Fact]
    public void UnknownDuration_RemainsOther()
    {
        var parsed = CodexRateLimitParser.Parse(null, JsonNode.Parse("""
            { "rateLimits": { "primary": { "usedPercent": 5, "windowDurationMins": 1440 } } }
            """));
        Assert.Equal(CodexWindowKind.Other, parsed.Windows[0].Kind);
        Assert.Equal(1440, parsed.Windows[0].WindowDurationMinutes);
    }

    [Fact]
    public void InvalidPercent_BecomesUnavailableNotZero()
    {
        var parsed = CodexRateLimitParser.Parse(null, JsonNode.Parse("""
            { "rateLimits": { "primary": { "usedPercent": "NaN", "windowDurationMins": 300 } } }
            """));
        Assert.Null(parsed.Windows[0].UsedPercent);
        Assert.Null(parsed.Windows[0].RemainingPercent);
    }

    [Fact]
    public void OutOfRangePercent_IsClamped()
    {
        var parsed = CodexRateLimitParser.Parse(null, JsonNode.Parse("""
            { "rateLimits": { "primary": { "usedPercent": 150, "windowDurationMins": 300 } } }
            """));
        Assert.Equal(100, parsed.Windows[0].UsedPercent);
        Assert.Equal(0, parsed.Windows[0].RemainingPercent);
    }

    [Fact]
    public void InvalidResetTimestamp_IsUnavailable()
    {
        var parsed = CodexRateLimitParser.Parse(null, JsonNode.Parse("""
            { "rateLimits": { "primary": { "usedPercent": 10, "windowDurationMins": 300, "resetsAt": -5 } } }
            """));
        Assert.Null(parsed.Windows[0].ResetsAt);
        Assert.Equal(10, parsed.Windows[0].UsedPercent);
    }

    [Fact]
    public void UnknownProperties_AreIgnored()
    {
        var parsed = CodexRateLimitParser.Parse(null, JsonNode.Parse("""
            {
              "rateLimits": {
                "primary": { "usedPercent": 3, "windowDurationMins": 300, "secretToken": "nope", "email": "a@b.c" },
                "mystery": { "hello": true }
              }
            }
            """));
        Assert.Equal(CodexQuotaStatus.Available, parsed.Status);
        Assert.Single(parsed.Windows);
        Assert.Equal(3, parsed.Windows[0].UsedPercent);
    }

    [Fact]
    public void SignedOutAccount_DoesNotInventPercents()
    {
        var parsed = CodexRateLimitParser.Parse(JsonNode.Parse("""{"loggedIn":false}"""), JsonNode.Parse("""{"rateLimits":null}"""));
        Assert.Equal(CodexQuotaStatus.SignedOut, parsed.Status);
        Assert.Empty(parsed.Windows);
    }

    [Fact]
    public void AccountReadProtocolError_DoesNotFailWhenRateLimitsParse()
    {
        var parsed = CodexRateLimitParser.Parse(
            JsonNode.Parse("""{"id":2,"error":{"code":-32600,"message":"Invalid request: missing field `params`"}}"""),
            JsonNode.Parse("""
            {
              "id": 3,
              "result": {
                "rateLimits": {
                  "primary": { "usedPercent": 97, "windowDurationMins": 10080, "resetsAt": 1893456000 },
                  "secondary": null
                },
                "rateLimitsByLimitId": {
                  "codex_extra": { "primary": { "usedPercent": 0, "windowDurationMins": 300 } },
                  "codex": { "primary": { "usedPercent": 97, "windowDurationMins": 10080, "resetsAt": 1893456000 } }
                },
                "rateLimitResetCredits": { "availableCount": 3 }
              }
            }
            """));
        Assert.Equal(CodexQuotaStatus.Available, parsed.Status);
        Assert.Equal(97, parsed.Windows[0].UsedPercent);
        Assert.Equal(CodexWindowKind.Weekly, parsed.Windows[0].Kind);
        Assert.Equal(3, parsed.ResetCreditsAvailable);
    }

    [Fact]
    public void LiveAccountShapeWithoutLoggedIn_ReadsPlanType()
    {
        var parsed = CodexRateLimitParser.Parse(
            JsonNode.Parse("""{"id":2,"result":{"account":{"planType":"plus"},"requiresOpenaiAuth":true}}"""),
            JsonNode.Parse("""{"id":3,"result":{"rateLimits":{"primary":{"usedPercent":12,"windowDurationMins":300}}}}"""));
        Assert.Equal(CodexQuotaStatus.Available, parsed.Status);
        Assert.Equal("plus", parsed.PlanType);
        Assert.Null(parsed.OrdinaryUsageAllowed);
    }

    [Theory]
    [InlineData("300.5")]
    [InlineData("2147483648")]
    [InlineData("2147483647.0000001")]
    [InlineData("-1")]
    public void InvalidWindowDuration_FailsBeforeIntegerConversion(string duration)
    {
        var parsed = CodexRateLimitParser.Parse(
            null,
            JsonNode.Parse("""{"rateLimits":{"primary":{"usedPercent":10,"windowDurationMins":""" + duration + """}}}"""));

        Assert.Equal(CodexQuotaStatus.ProtocolMismatch, parsed.Status);
        Assert.Empty(parsed.Windows);
    }
    [Fact]
    public void MaximumIntWindowDuration_IsAccepted()
    {
        var parsed = CodexRateLimitParser.Parse(
            null,
            JsonNode.Parse("""{"rateLimits":{"primary":{"usedPercent":10,"windowDurationMins":2147483647}}}"""));

        Assert.Equal(CodexQuotaStatus.Available, parsed.Status);
        Assert.Equal(int.MaxValue, parsed.Windows[0].WindowDurationMinutes);
    }
}
