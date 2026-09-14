using System.Text;
using System.Text.Json.Nodes;

namespace CycleArc.Codex;

public sealed record CodexLoginResult(CodexQuotaStatus Status, CodexAccountIdentity? Identity = null, string? Detail = null);

public static class CodexLoginUrl
{
    public static bool IsAllowed(string? value, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || value.Contains('\\')
            || !Uri.TryCreate(value, UriKind.Absolute, out var candidate)
            || candidate.Scheme != Uri.UriSchemeHttps || !candidate.IsDefaultPort || candidate.UserInfo.Length != 0
            || candidate.Host is not ("auth.openai.com" or "chatgpt.com")) return false;
        uri = candidate;
        return true;
    }
}

public sealed partial class CodexAppServerClient
{
    // Only managed ChatGPT OAuth. No tokens enter or leave this API.
    public async Task<CodexLoginResult> LoginAsync(CodexLaunchCommand command, string version,
        Func<Uri, CancellationToken, Task> openBrowser, CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout ?? TimeSpan.FromMilliseconds(CodexProtocol.LoginHardCeilingMs));
        ICodexProcess? process = null;
        string? loginId = null;
        var completed = false;
        var sent = new List<string>();
        var notifications = new Queue<JsonNode>();
        try
        {
            process = await StartAsync(command, bounded.Token).ConfigureAwait(false);
            // Login diagnostics may contain URLs and account information. Discard them.
            _ = process.DrainStderrAsync(new StringBuilder(), CodexProtocol.MaxStderrBytes, bounded.Token);
            await SendAsync(process, CodexProtocol.BuildInitialize(version), "initialize", sent, bounded.Token).ConfigureAwait(false);
            var init = await WaitForResponseAsync(process, "1", CodexProtocol.InitializeTimeoutMs, bounded.Token).ConfigureAwait(false);
            if (init.Status != CodexQuotaStatus.Available) return new(init.Status);
            if (CodexProtocol.HasError(init.Node) || init.Node?["result"] is not JsonObject)
                return new(CodexQuotaStatus.ProtocolMismatch);
            await SendAsync(process, CodexProtocol.BuildInitialized(), "initialized", sent, bounded.Token).ConfigureAwait(false);
            var start = new JsonObject { ["id"] = 10, ["method"] = "account/login/start",
                ["params"] = new JsonObject { ["type"] = "chatgpt" } };
            await SendAsync(process, start.ToJsonString(), "account/login/start", sent, bounded.Token).ConfigureAwait(false);
            var response = await WaitForResponseAsync(process, "10", CodexProtocol.AccountReadTimeoutMs, bounded.Token, node =>
            {
                if (node["method"]?.ToString() != "account/login/completed") return;
                if (notifications.Count >= 8) throw new CodexProtocolException("Too many login notifications.");
                notifications.Enqueue(node);
            }).ConfigureAwait(false);
            if (response.Status != CodexQuotaStatus.Available) return new(response.Status);
            if (CodexProtocol.HasError(response.Node)) return new(CodexQuotaStatus.Unavailable);
            if (response.Node?["result"] is not JsonObject result || result["type"]?.ToString() != "chatgpt"
                || result["loginId"] is not JsonValue idValue || !idValue.TryGetValue<string>(out loginId)
                || !Guid.TryParse(loginId, out _)) return new(CodexQuotaStatus.ProtocolMismatch);
            if (result["authUrl"] is not JsonValue urlValue || !urlValue.TryGetValue<string>(out var url)
                || !CodexLoginUrl.IsAllowed(url, out var uri)) return new(CodexQuotaStatus.ProtocolMismatch);
            await openBrowser(uri!, bounded.Token).WaitAsync(bounded.Token).ConfigureAwait(false);

            while (true)
            {
                var node = notifications.TryDequeue(out var early) ? early
                    : CodexProtocol.ParseLine(await process.ReadLineAsync(CodexProtocol.MaxJsonLineBytes, bounded.Token)
                        .ConfigureAwait(false) ?? throw new IOException("Login connection closed."));
                if (node?["method"]?.ToString() != "account/login/completed") continue;
                if (node["params"] is not JsonObject data
                    || data["loginId"] is not JsonValue notificationId || !notificationId.TryGetValue<string>(out var value))
                    return new(CodexQuotaStatus.ProtocolMismatch);
                if (value != loginId) continue;
                if (data["success"] is not JsonValue successValue || !successValue.TryGetValue<bool>(out var success))
                    return new(CodexQuotaStatus.ProtocolMismatch);
                completed = true;
                if (!success) return new(CodexQuotaStatus.Unavailable);
                // A completion notification is insufficient: verify the resulting account.
                await SendAsync(process, CodexProtocol.BuildAccountRead(), "account/read", sent, bounded.Token).ConfigureAwait(false);
                var account = await WaitForResponseAsync(process, "2", CodexProtocol.AccountReadTimeoutMs, bounded.Token).ConfigureAwait(false);
                if (account.Status != CodexQuotaStatus.Available) return new(account.Status);
                var identity = CodexAccountIdentity.Parse(account.Node);
                return new(identity.Status, identity);
            }
        }
        catch (OperationCanceledException) { return new(cancellationToken.IsCancellationRequested ? CodexQuotaStatus.Cancelled : CodexQuotaStatus.TimedOut); }
        catch (TimeoutException) { return new(CodexQuotaStatus.TimedOut); }
        catch (FileNotFoundException) { return new(CodexQuotaStatus.CodexNotFound); }
        catch (Exception ex) when (ex is CodexProtocolException or InvalidOperationException) { return new(CodexQuotaStatus.ProtocolMismatch); }
        catch { return new(CodexQuotaStatus.Unavailable); }
        finally
        {
            if (process is not null)
            {
                if (loginId is not null && !completed)
                {
                    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try
                    {
                        var request = new JsonObject { ["id"] = 11, ["method"] = "account/login/cancel",
                            ["params"] = new JsonObject { ["loginId"] = loginId } };
                        await SendAsync(process, request.ToJsonString(), "account/login/cancel", sent, cancel.Token).ConfigureAwait(false);
                        await WaitForResponseAsync(process, "11", 2000, cancel.Token).ConfigureAwait(false);
                    }
                    catch { }
                }
                await CompleteAsync(CodexQuotaStatus.Unavailable, null, null, sent, new StringBuilder(), null, process).ConfigureAwait(false);
            }
        }
    }
}
