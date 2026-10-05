using System.Text.Json;

namespace AvaMedia.Core;
public sealed class Storage
{
    private readonly string _root;
    private static readonly JsonSerializerOptions Json = new() {WriteIndented=true};
    public Storage(string? root = null) { _root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AvaMedia"); Directory.CreateDirectory(_root); }
    public AppSettings LoadSettings() => Read<AppSettings>("settings.json") ?? new();
    public List<Job> LoadJobs()
    {
        var jobs=Read<List<Job>>("queue.json") ?? [];
        foreach(var j in jobs.Where(j=>j.State == JobState.Running)) {j.State=JobState.Cancelled;j.Error="应用在任务完成前退出，可重试。";}
        return jobs;
    }
    public void SaveSettings(AppSettings s) => Write("settings.json",s);
    public void SaveJobs(IEnumerable<Job> jobs) => Write("queue.json",jobs.ToArray());
    public Dictionary<string,ConversionOptions> LoadPresets() => Read<Dictionary<string,ConversionOptions>>("presets.json")??[];
    public void SavePreset(string key,ConversionOptions options){var presets=LoadPresets();presets[key]=options.Clone();Write("presets.json",presets);}
    private T? Read<T>(string name)
    { try {return JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(_root,name)));} catch(IOException) {return default;} catch(JsonException) {return default;} }
    private void Write<T>(string name,T value)
    {
        var path=Path.Combine(_root,name); var temp=path+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(value,Json)); File.Move(temp,path,true);
    }
}
