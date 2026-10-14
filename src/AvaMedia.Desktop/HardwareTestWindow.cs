using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using AvaMedia.Core;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace AvaMedia.Desktop;

public sealed class HardwareTestWindow : Window
{
    private readonly string _configured;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TextBox _log;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _closed;
    private static HardwareTestWindow? _open;
    public Task Ready => _ready.Task;

    internal static Task ShowSingleAsync(string configured)
    {
        if (_open is { } existing)
        {
            Controls.WindowChrome.Present(existing);
            return Controls.WindowChrome.WhenClosed(existing);
        }
        var window = new HardwareTestWindow(configured);
        _open = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_open, window)) _open = null; };
        return Controls.WindowChrome.ShowIndependentAsync(window);
    }
    public IReadOnlyList<HardwareEncoderResult> Report { get; private set; } = [];
    public HardwareTestWindow(string configured)
    {
        _configured = configured; Title = "GPU 加速能力"; Width = 580; Height = 392; MinWidth = 460; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { RowDefinitions = new("*,Auto"), Margin = new(12), RowSpacing = 8 };
        _log = new() { Name = "HardwareLog", Classes={"log"}, IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.NoWrap, Text = "正在检测本平台的 GPU 编码能力…" };
        root.Bind(MarginProperty,new DynamicResourceExtension("UiPagePadding"));root.Bind(Grid.RowSpacingProperty,new DynamicResourceExtension("AcGap"));
        root.Children.Add(Ui.ComponentCard(_log));
        var ok = new Button { Name = "OkButton", Content = "关闭", Classes={ "ac","dialog-action"}, HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true, IsCancel = true };
        ok.Click += (_, _) => Close(); Grid.SetRow(ok, 1); root.Children.Add(ok); Content = root;
        Opened += async (_, _) => await Test();
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); if (_ready.Task.IsCompleted) _lifetime.Dispose(); };
    }
    private async Task Test()
    {
        var lines = new List<string>();
        try
        {
            var executable = MediaEngine.Resolve(_configured, "ffmpeg");
            Report = await HardwareAcceleration.TestAsync(executable, _lifetime.Token, true, result =>
                Dispatcher.UIThread.Post(() => { if (_closed) return; lines.Add(ResultText(result)); _log.Text = string.Join(Environment.NewLine, lines); }));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Report = HardwareAcceleration.Encoders.Select(e => new HardwareEncoderResult(e.Name, e.Codec, false, ex.Message)).ToArray(); }
        finally
        {
            if (!_closed)
            {
                _log.Text = string.Join(Environment.NewLine, Report.Select(ResultText));
            }
            _ready.TrySetResult(); if (_closed) _lifetime.Dispose();
        }
    }
    private static string ResultText(HardwareEncoderResult result) => result.Summary
        + (!result.Supported && result.Detail.Length > 0 ? Environment.NewLine + result.Detail : "");
}
