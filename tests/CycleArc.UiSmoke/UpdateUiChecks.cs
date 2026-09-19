using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CycleArc.Models;
using CycleArc.Services;
using CycleArc.UI;
using CycleArc.Updates;

namespace CycleArc.UiSmoke;

internal static class UpdateUiChecks
{
    public static void Run()
    {
        var applyTheme = typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)!;
        var output = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "update-ui");
        Directory.CreateDirectory(output);
        var previousLanguage = UiText.Language;
        try
        {
            foreach (var language in new[] { UiLanguage.English, UiLanguage.Korean })
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                UiText.SetLanguage(language);
                applyTheme.Invoke(null, [theme]);
                var client = new PreviewClient();
                var coordinator = new AppUpdateCoordinator(client, maxAttempts: 1);
                coordinator.CheckAsync().GetAwaiter().GetResult();
                var exited = false;
                var window = new UpdateWindow(coordinator, () => exited = true);
                try
                {
                    var action = (Button)window.FindName("ActionButton");
                    var status = (TextBlock)window.FindName("StatusLabel");
                    Check(action.IsEnabled && action.Content as string == UiText.T("Download update", "업데이트 다운로드"), "Available update action is missing.");
                    Check(client.Downloads == 0 && client.Applies == 0 && !exited, "Opening updates downloaded or applied without approval.");
                    Render(window, output, language, theme);
                    action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(client.Downloads == 1 && coordinator.State == AppUpdateState.Ready,
                        "Download approval did not leave the verified update waiting for a separate restart approval.");
                    Check(client.Applies == 0 && !exited, "Download unexpectedly applied the update.");
                    Check(action.Content as string == UiText.T("Restart & update", "재시작 후 적용"), "Ready state is not localized.");
                    Check(!string.IsNullOrWhiteSpace(status.Text), "Update status is empty.");
                    action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    PumpUntil(() => exited);
                    Check(client.Applies == 1 && exited, "Restart approval did not hand off and exit.");
                }
                finally { window.Close(); }
            }
            RunCanceledDownloadLifecycle();
            var disabled = new AppUpdateCoordinator(new PreviewClient { IsInstalled = false });
            using var lifetime = new CancellationTokenSource();
            var developmentWindow = new UpdateWindow(disabled, () => throw new InvalidOperationException("Development app exited."), lifetime.Token);
            try
            {
                Check(!((Button)developmentWindow.FindName("CheckButton")).IsEnabled, "Loose development build can check updates.");
                Check(((Button)developmentWindow.FindName("ActionButton")).Visibility == Visibility.Collapsed, "Loose development build can install updates.");
            }
            finally { developmentWindow.Close(); }
            Console.WriteLine($"PASS: update approval UI, development isolation, EN/KO + Dark/Light layouts; previews: {output}");
        }
        finally { UiText.SetLanguage(previousLanguage); }
    }

    private static void RunCanceledDownloadLifecycle()
    {
        var client = new CancellationProbeClient();
        var coordinator = new AppUpdateCoordinator(client, maxAttempts: 1);
        coordinator.CheckAsync().GetAwaiter().GetResult();
        var exited = false;
        var window = new UpdateWindow(coordinator, () => exited = true);
        Task? operation = null;
        var closed = false;
        window.OperationStarted += started => operation = started;
        try
        {
            ((Button)window.FindName("ActionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => client.DownloadStarted.Task.IsCompleted && operation is not null);
            Check(operation is not null && !operation.IsCompleted,
                "Update download operation was not retained while the package was pending.");

            window.Close();
            closed = true;
            PumpUntil(() => client.CleanupStarted.Task.IsCompleted);
            Check(!operation!.IsCompleted,
                "Closing the update window did not keep its canceled download alive through cleanup.");

            client.ReleaseCleanup();
            PumpUntil(() => operation.IsCompleted);
            operation.GetAwaiter().GetResult();
            Check(coordinator.State == AppUpdateState.Failed
                && coordinator.Error == AppUpdateError.Canceled,
                "Canceled update download did not settle in the canceled state.");
            Check(!exited && client.Applies == 0,
                "Canceling an update unexpectedly applied the package or exited the app.");
        }
        finally
        {
            client.ReleaseCleanup();
            if (!closed) window.Close();
            if (operation is not null)
            {
                try { operation.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
            }
        }
    }

    private static void Render(UpdateWindow window, string directory, UiLanguage language, AppTheme theme)
    {
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        var frame = new Border { Width = 480, Height = 510, Background = (Brush)window.FindResource("BgBrush"), Child = content };
        TextElement.SetForeground(frame, (Brush)window.FindResource("TextBrush"));
        frame.Measure(new Size(frame.Width, frame.Height));
        frame.Arrange(new Rect(0, 0, frame.Width, frame.Height));
        frame.UpdateLayout();
        foreach (var name in new[] { "Heading", "StatusLabel", "CheckButton", "LaterButton", "ActionButton" })
        {
            var element = (FrameworkElement)window.FindName(name);
            var rect = element.TransformToAncestor(frame).TransformBounds(new Rect(element.RenderSize));
            Check(rect.Left >= 0 && rect.Top >= 0 && rect.Right <= frame.Width + .5 && rect.Bottom <= frame.Height + .5,
                $"Update view clips {name} ({language}/{theme}).");
        }
        var bitmap = new RenderTargetBitmap((int)frame.Width * 2, (int)frame.Height * 2, 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(frame);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(directory, $"updates-{(language == UiLanguage.Korean ? "ko" : "en")}-{theme.ToString().ToLowerInvariant()}.png")))
            encoder.Save(file);
        frame.Child = null;
        window.Content = content;
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }

    private static void PumpUntil(Func<bool> finished)
    {
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (finished() || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Check(finished(), "Update handoff did not finish within five seconds.");
    }

    private sealed class PreviewClient : IAppUpdateClient
    {
        public bool IsInstalled { get; init; } = true;
        public string CurrentVersion => "0.6.0";
        public int Downloads { get; private set; }
        public int Applies { get; private set; }
        public Task<AppUpdateRelease?> CheckAsync(CancellationToken token) => Task.FromResult<AppUpdateRelease?>(new("0.6.1",
            UiText.T("CycleArc 0.6.1\n\n• Compare accounts in the desktop widget\n• Resize the popup and widget independently\n• Follow installation progress in the setup window\n\nSample upgrade from 0.6.0 to 0.6.1. No download is performed in this preview.",
                "CycleArc 0.6.1\n\n• 데스크톱 위젯에서 여러 계정 비교\n• 팝업과 위젯 크기를 각각 조절\n• 설치 화면에서 진행 상태 확인\n\n0.6.0에서 0.6.1로 업데이트하는 예시입니다. 이 미리보기는 다운로드하지 않습니다.")));
        public Task DownloadAsync(AppUpdateRelease release, IProgress<int> progress, CancellationToken token)
        {
            Downloads++;
            progress.Report(100);
            return Task.CompletedTask;
        }
        public void ApplyOnExit(AppUpdateRelease release) => Applies++;
    }

    private sealed class CancellationProbeClient : IAppUpdateClient
    {
        public bool IsInstalled => true;
        public string CurrentVersion => "0.6.0";
        public int Applies { get; private set; }
        public TaskCompletionSource<object?> DownloadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<object?> CleanupStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<object?> CleanupReleased { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AppUpdateRelease?> CheckAsync(CancellationToken token) =>
            Task.FromResult<AppUpdateRelease?>(new("0.6.1", "pending download"));

        public async Task DownloadAsync(AppUpdateRelease release, IProgress<int> progress, CancellationToken token)
        {
            DownloadStarted.TrySetResult(null);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
                CleanupStarted.TrySetResult(null);
                await CleanupReleased.Task.ConfigureAwait(false);
                throw;
            }
        }

        public void ApplyOnExit(AppUpdateRelease release) => Applies++;

        public void ReleaseCleanup() => CleanupReleased.TrySetResult(null);
    }
}
