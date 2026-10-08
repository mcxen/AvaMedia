using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed class MediaFileEntry : Observable
{
    private string _path;
    private bool _include = true;
    private string _newName = "";
    private string _status = "待处理";
    private string _keyword = "";
    private string _details;
    public string Path => _path;
    public string Name => System.IO.Path.GetFileName(_path);
    public string Kind => Localization.Text(new MediaFileRouter().Classify(_path) switch { MediaFileKind.Image => "图片", MediaFileKind.Video => "视频", _ => "文件" });
    public bool Include { get => _include; set => Set(ref _include, value); }
    public string NewName { get => _newName; set => Set(ref _newName, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string Keyword { get => _keyword; set => Set(ref _keyword, value); }
    public string Details { get => _details; set => Set(ref _details, value); }
    public string? LastSheet { get; set; }
    public MediaFileEntry(string path) { _path = path; _details = path; }
    public void Renamed(string path) { _path = path; NewName = ""; Details = path; Raise(nameof(Path)); Raise(nameof(Name)); Raise(nameof(Kind)); }
    public void RefreshLanguage() => Raise(nameof(Kind));
}
