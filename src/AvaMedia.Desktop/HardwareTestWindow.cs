using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed class HardwareTestWindow : Window
{
    private readonly string _configured;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TextBox _log;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _closed;
    public Task Ready => _ready.Task;
    public IReadOnlyList<HardwareEncoderResult> Report { get; private set; } = [];
    public HardwareTestWindow(string configured)
    {
        _configured = configured; Title = "GPU 加速能力"; Width = 580; Height = 392; MinWidth = 460; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { RowDefinitions = new("*,Auto"), Margin = new(20), RowSpacing = 12 };
        _log = new() { Name = "HardwareLog", Classes={"log"}, IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.NoWrap, Text = "正在检测本平台的 GPU 编码能力…" };
        root.Children.Add(_log);
        var ok = new Button { Name = "OkButton", Content = "关闭", Classes={"dialog-action"}, HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true, IsCancel = true };
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
                Dispatcher.UIThread.Post(() => { if (_closed) return; lines.Add(result.Summary); _log.Text = string.Join(Environment.NewLine, lines); }));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Report = HardwareAcceleration.Encoders.Select(e => new HardwareEncoderResult(e.Name, e.Codec, false, ex.Message)).ToArray(); }
        finally
        {
            if (!_closed)
            {
                _log.Text = string.Join(Environment.NewLine, Report.Select(r => r.Summary));
                ToolTip.SetTip(_log, string.Join(Environment.NewLine + Environment.NewLine, Report.Select(r => r.Name + ": " + r.Detail)));
            }
            _ready.TrySetResult(); if (_closed) _lifetime.Dispose();
        }
    }
}
