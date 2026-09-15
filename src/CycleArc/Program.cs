using System.IO;
using System.Text;
using CycleArc.Codex;
using CycleArc.Providers.Claude;

namespace CycleArc;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.FirstOrDefault() == ClaudeFailureCommand.Argument)
        {
            try
            {
                if (args is not [ClaudeFailureCommand.Argument, var payload]) return 1;
                var options = ClaudeFailureCommand.Decode(payload);
                using var input = Console.OpenStandardInput();
                using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
                return ClaudeFailureBridge.RunAsync(options, input, output, new CodexAccountStore(options.DataRoot)).GetAwaiter().GetResult();
            }
            catch { return 1; }
        }
        if (args.FirstOrDefault() == ClaudeStatusLineBridge.Argument)
        {
            try
            {
                if (args is not [ClaudeStatusLineBridge.Argument, var payload]) return 2;
                var options = ClaudeStatusLineInstaller.Decode(payload);
                using var input = Console.OpenStandardInput();
                using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
                return ClaudeStatusLineBridge.RunAsync(options, input, output, new CodexAccountStore(options.DataRoot)).GetAwaiter().GetResult();
            }
            catch { return 1; }
        }
        if (args.FirstOrDefault() == ClaudeStatusLineCommand.Argument)
        {
            // Run before WPF, the single-instance mutex, settings, tray or Codex startup.
            // All invocations target only an existing Claude profile's projected inbox.
            try
            {
                // An explicit registry root supports isolated, synthetic process checks.
                // It affects only this collector call, never desktop settings or credentials.
                string? root = null;
                if (args is [ClaudeStatusLineCommand.Argument, var id, "--data-root", var suppliedRoot])
                {
                    if (!Path.IsPathFullyQualified(suppliedRoot)) return 2;
                    root = suppliedRoot;
                    args = [ClaudeStatusLineCommand.Argument, id];
                }
                using var input = Console.OpenStandardInput();
                using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
                return ClaudeStatusLineCommand.RunAsync(args, input, output, new CodexAccountStore(root)).GetAwaiter().GetResult();
            }
            catch { return 1; } // Never emit raw input, exception text or paths.
        }
        return Services.DesktopBootstrap.Run(args);
    }
}
