using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

internal sealed class TrackSelector : ComboBox
{
    private sealed record Track(int Index, string Label);
    private int _revision;
    public void SetSource(IMediaEngine engine,string? path,string type,int selected,CancellationToken lifetime)
    {if(!string.IsNullOrWhiteSpace(path))_=LoadAsync(engine,path,type,selected,lifetime);}
    public int Index => (SelectedItem as Track)?.Index ?? throw new ArgumentException("请选择媒体轨道。");
    public TrackSelector(IMediaEngine? engine, string? path, string type, int selected, CancellationToken lifetime = default)
    {
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        ItemTemplate = new FuncDataTemplate<Track>((track, _) => { var text = Ui.Text(track?.Label ?? ""); Localization.SetIsUserText(text, true); return text; });
        var first = new Track(selected, selected<0?Localization.Text("自动选择"):Localization.Format($"第 {selected + 1} 条")); ItemsSource = new[] { first }; SelectedItem = first;
        if(engine is not null && !string.IsNullOrWhiteSpace(path)) _ = LoadAsync(engine, path, type, selected, lifetime);
    }
    private async Task LoadAsync(IMediaEngine engine, string path, string type, int selected, CancellationToken lifetime)
    {
        var revision=++_revision;
        try
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime); operation.CancelAfter(TimeSpan.FromSeconds(30));
            var info = await engine.Probe(path, operation.Token); using var json = JsonDocument.Parse(info.RawJson);
            var tracks = json.RootElement.GetProperty("streams").EnumerateArray()
                .Where(stream => stream.TryGetProperty("codec_type", out var value) && value.GetString() == type)
                .Select((stream,index)=>(Stream:stream,Index:index)).Where(item=>type!="video"||MediaStreams.IsContentVideo(item.Stream))
                .Select(item=>new Track(item.Index,Label(item.Stream,item.Index))).ToArray();
            if(lifetime.IsCancellationRequested||revision!=_revision) return;
            if(tracks.Length == 0) { ToolTip.SetTip(this, Localization.Text("源文件没有此类轨道")); return; }
            if(selected<0)tracks=[new Track(selected,Localization.Text("自动选择")),..tracks];
            ItemsSource = tracks; SelectedItem = tracks.FirstOrDefault(track => track.Index == selected) ?? tracks[0];
        }
        catch(Exception error) { if(!lifetime.IsCancellationRequested&&revision==_revision) ToolTip.SetTip(this, error.Message); }
    }
    internal static string Label(JsonElement stream, int index)
    {
        var parts = new List<string> { Localization.Format($"第 {index + 1} 条") };
        if(stream.TryGetProperty("codec_name", out var codec)) parts.Add(codec.GetString()?.ToUpperInvariant() ?? "");
        if(stream.TryGetProperty("tags", out var tags))
        {
            if(tags.TryGetProperty("language", out var language)) parts.Add(language.GetString() ?? "");
            if(tags.TryGetProperty("title", out var title)) parts.Add(title.GetString() ?? "");
        }
        return string.Join(" · ", parts.Where(part => part.Length > 0));
    }
}
