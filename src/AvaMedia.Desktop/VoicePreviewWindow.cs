using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

internal sealed class VoicePreviewWindow : Window
{
    private readonly IMediaEngine _engine;
    private readonly string _source;
    private readonly ConversionOptions _options;
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "AvaMedia-voice-preview-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TextBlock _status = Ui.Text("", "caption");
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Spacing = 10 };
    private PlayerWindow? _player;
    public VoicePreviewWindow(IMediaEngine engine, string source, ConversionOptions options)
    {
        _engine = engine; _source = source; _options = options.Clone();
        Title = "人声试听 · 10 秒"; Width = 450; Height = 240; MinWidth = 400; MinHeight = 220; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new StackPanel { Margin = new(20), Spacing = 16 }; root.Children.Add(_status);
        _actions.Children.Add(Ui.Button("试听原声", async () => await PlayAsync(false))); _actions.Children.Add(Ui.Button("试听增强", async () => await PlayAsync(true)));
        root.Children.Add(_actions); root.Children.Add(Ui.DialogButton("关闭", Close)); Content = root;
        Closed += (_, _) => { _lifetime.Cancel(); _player?.Close(); _ = CleanupAsync(); };
    }
    private Task _preparing = Task.CompletedTask;
    private async Task PlayAsync(bool enhanced)
    {
        if(!_preparing.IsCompleted) return;
        _preparing = PrepareAndPlayAsync(enhanced); await _preparing;
    }
    private async Task PrepareAndPlayAsync(bool enhanced)
    {
        _actions.IsEnabled = false;
        try
        {
            var output = Path.Combine(_temporary, enhanced ? "enhanced.wav" : "original.wav"); Directory.CreateDirectory(_temporary);
            if(!File.Exists(output))
            {
                var info = await _engine.Probe(_source, _lifetime.Token, audioStreamIndex: _options.AudioStreamIndex);
                if(!info.HasAudio) throw new ArgumentException("没有可试听的音轨。");
                var options = _options.Clone(); options.Format = "wav"; options.CopyStreams = false; options.AudioCodec = "pcm_s16le";
                options.Transcription = null; options.VoiceEnhancement = enhanced; options.End = Math.Min(info.Duration, options.Start + 10);
                await _engine.Execute(new() { FeatureId = "audio-wav", Inputs = [_source], Output = output, Options = options },
                    percent => Dispatcher.UIThread.Post(() => { if(!_lifetime.IsCancellationRequested) _status.Text = Localization.Text("准备试听") + $" · {percent:0}%"; }), _lifetime.Token);
            }
            if(_lifetime.IsCancellationRequested) return;
            if(_player is null) { _player = new PlayerWindow(_engine); _player.Closed += (_, _) => _player = null; _player.ShowForPlayback(this); }
            await _player.OpenAtAsync(output, 0); _player.Activate(); _status.Text = Localization.Text(enhanced ? "试听增强" : "试听原声");
        }
        catch(OperationCanceledException) { }
        catch(Exception error) { if(!_lifetime.IsCancellationRequested) _status.Text = error.Message; }
        finally { if(!_lifetime.IsCancellationRequested) _actions.IsEnabled = true; }
    }
    private async Task CleanupAsync()
    {
        await _preparing;
        try { if(Directory.Exists(_temporary)) Directory.Delete(_temporary, true); } catch(IOException) { } catch(UnauthorizedAccessException) { }
    }
}
