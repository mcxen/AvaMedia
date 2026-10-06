using Avalonia.Media.Imaging;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public interface IPlaybackSession : IDisposable
{
    WriteableBitmap Frame { get; }
    bool IsPlaying { get; }
    bool IsPaused { get; }
    bool HasSession { get; }
    bool PresentationVisible { get; set; }
    float Volume { get; set; }
    bool Muted { get; set; }
    double Speed { get; set; }
    Task FirstFrame { get; }
    event Action<double>? Updated;
    event Action? Finished;
    event Action<string>? Error;
    void Configure(MediaInfo info);
    Task Play(double seconds, bool video, double end);
    void Pause();
    void Resume();
    Task Stop();
}
