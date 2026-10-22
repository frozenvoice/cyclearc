using System.Text.RegularExpressions;

namespace CycleArc.Tests;

/// <summary>
/// Every UiText.T(English, Korean) pair in the product source has a real Korean side: not
/// empty, not a copy of the English, and written in Hangul unless it is only names, numbers
/// or symbols. A missing translation fails here instead of showing English in the Korean UI.
/// </summary>
public class TranslationAuditTests
{
    private static readonly Regex Pair = new(
        "UiText\\.T\\(\\s*\\$?\"((?:[^\"\\\\]|\\\\.)*)\"\\s*,\\s*\\$?\"((?:[^\"\\\\]|\\\\.)*)\"\\s*\\)",
        RegexOptions.Compiled);

    // Words that stay in English in Korean text: product, provider and feature names.
    private static readonly string[] Names =
    [
        "CycleArc", "Codex", "Claude", "Cursor", "ChatGPT", "OpenAI", "Anthropic", "GitHub", "Windows", "Desktop",
        "Code", "Pro", "Max", "Plus", "Team", "Grok", "Auto", "API", "CLI", "statusLine", "StopFailure", "hooks", "json",
        "Usage", "Limit", "Reset", "Sol", "GPT", "WebView", "Edge", "Chrome", "OAuth", "USD", "CSV", "Ctrl", "Alt", "Shift",
        "Esc", "Enter", "Space", "Tab", "PowerShell", "Velopack", "Models", "Other", "Settings", "Usage page", "on-demand"
    ];

    [Fact]
    public void EveryKoreanTranslationExistsAndIsKorean()
    {
        var problems = new List<string>();
        var pairs = 0;
        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);
            foreach (Match match in Pair.Matches(text))
            {
                pairs++;
                var where = Path.GetFileName(file) + ":" + (text[..match.Index].Count(c => c == '\n') + 1);
                if (Problem(match.Groups[1].Value, match.Groups[2].Value) is { } problem) problems.Add(where + " " + problem);
            }
        }
        // The scan must actually see the product's strings, not pass by matching nothing.
        Assert.True(pairs > 500, $"Only {pairs} UiText.T pairs were found; the audit pattern no longer matches the source.");
        Assert.True(problems.Count == 0, "Translation problems:\n" + string.Join("\n", problems));
    }

    [Theory]
    [InlineData("UiText.T(\"Refresh all\", \"\")", "has no Korean")]
    [InlineData("UiText.T(\"Refresh all\", \"Refresh all\")", "copies the English")]
    [InlineData("UiText.T($\"{count} accounts left\", $\"{count} accounts left!\")", "English-only Korean")]
    [InlineData("UiText.T(\"Open Claude Usage\", \"Claude Usage 열기\")", null)]
    [InlineData("UiText.T(\"Codex · Claude\", \"Codex · Claude\")", null)]
    [InlineData("UiText.T(\"\", \"개\")", null)]
    public void TheAuditRecognisesMissingAndCopiedTranslations(string source, string? expected)
    {
        var match = Pair.Match(source);
        Assert.True(match.Success);
        var problem = Problem(match.Groups[1].Value, match.Groups[2].Value);
        if (expected is null) Assert.Null(problem);
        else Assert.Contains(expected, problem, StringComparison.Ordinal);
    }

    private static string? Problem(string english, string korean)
    {
        if (english.Length > 0 && korean.Length == 0) return $"has no Korean for \"{english}\"";
        if (WordsOf(english).Any() && english == korean) return $"copies the English \"{english}\"";
        if (WordsOf(english).Any() && !korean.Any(IsHangul) && WordsOf(korean).Any()) return $"has English-only Korean \"{korean}\"";
        return null;
    }

    // Words that would need translating: letters only, outside interpolation holes and names.
    private static IEnumerable<string> WordsOf(string value)
    {
        var withoutHoles = Regex.Replace(value, "\\{[^}]*\\}", " ");
        foreach (var name in Names.OrderByDescending(name => name.Length))
            withoutHoles = withoutHoles.Replace(name, " ", StringComparison.Ordinal);
        return Regex.Matches(withoutHoles, "[A-Za-z]{3,}").Select(match => match.Value);
    }

    private static bool IsHangul(char c) => c is >= '가' and <= '힣' or >= 'ㄱ' and <= 'ㆎ';

    private static IEnumerable<string> SourceFiles()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "CycleArc.sln"))) root = Path.GetDirectoryName(root)!;
        return Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }
}
