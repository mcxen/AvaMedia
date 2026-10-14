using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using AvaMedia.Core;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace AvaMedia.Mcp;

public sealed record McpSettingAccess(string Path, string Type, string Access, string ReadTool, string? WriteTool, string Argument, string Note);
public sealed record McpSettingsCatalog(McpSettingAccess[] Items, string[] Unsupported, JsonElement PatchSchema);

public sealed partial class McpTools
{
    [McpServerTool(Name = "avamedia_settings_catalog", Title = "设置逐项覆盖清单", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Enumerate every AppSettings field, nested remote-management field, online-provider field, merged desktop choice and word-library preference. Reports read/write/create-only/metadata/write-only semantics and exact tool argument routes. unsupported exposes newly added unmapped fields instead of silently omitting them. The native patch schema describes field types; platform-inactive fields and activation-key constraints remain explicit.")]
    public McpSettingsCatalog SettingsCatalog()
    {
        var rows = new List<McpSettingAccess>(); var unsupported = new List<string>();
        string Camel(string name) => JsonNamingPolicy.CamelCase.ConvertName(name);
        void Row(PropertyInfo p, string path, string access, string? writer, string argument, string note = "") =>
            rows.Add(new(path, p.PropertyType.Name, access, "avamedia_get_settings", writer, argument, note));
        foreach (var p in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.Name is nameof(AppSettings.OnlineAi) or nameof(AppSettings.Mcp)) continue;
            var path = Camel(p.Name);
            if (p.Name is nameof(AppSettings.LastUpdateCheck) or nameof(AppSettings.EffectiveCpuThreads))
                Row(p, path, "metadata", null, path, "由原生更新/资源服务计算或记录，只读。");
            else if (typeof(McpSettingsPatch).GetProperty(p.Name) is not null)
                Row(p, path, "readWrite", "avamedia_update_settings", "patch." + path,
                    p.Name == nameof(AppSettings.ExclusiveToolUnlocked) ? "启用须另传 activationKey；停用无需口令。"
                    : p.Name is nameof(AppSettings.FFmpegPath) or nameof(AppSettings.FFprobePath) or nameof(AppSettings.YtDlpPath) ? "仅使用外部工具的版本可写；内置版本中此选项不生效。"
                    : p.Name == nameof(AppSettings.LinuxInterfaceScalePercent) ? "Linux 下次启动生效。" : "");
            else { unsupported.Add(path); Row(p, path, "unsupported", null, ""); }
        }
        foreach (var p in typeof(McpSettings).GetProperties())
        {
            var path = "mcp." + Camel(p.Name);
            if (typeof(McpListenerPatch).GetProperty(p.Name) is null) unsupported.Add(path);
            Row(p, path, typeof(McpListenerPatch).GetProperty(p.Name) is null ? "unsupported" : "readWrite",
                "avamedia_update_settings", "patch." + path, "当前 HTTP 响应结束后切换；get_mcp_status 返回实际监听状态。");
        }
        foreach (var p in typeof(OnlineAiSettings).GetProperties())
        {
            if (p.Name == nameof(OnlineAiSettings.Providers)) continue;
            var supported = p.Name == nameof(OnlineAiSettings.DefaultProviderId);
            if (!supported) unsupported.Add("onlineAi." + Camel(p.Name));
            Row(p, "onlineAi." + Camel(p.Name), supported ? "readWrite" : "unsupported",
                supported ? "avamedia_update_provider" : null, "providerId + makeDefault");
        }
        foreach (var p in typeof(OnlineAiOptions).GetProperties())
        {
            var path = "onlineAi.providers[]." + Camel(p.Name);
            if (p.Name == nameof(OnlineAiOptions.ApiKey))
                Row(p, path, "writeOnly", "avamedia_update_provider", "providerId + apiKey", "只读 hasApiKey；空字符串删除，省略保留。");
            else if (p.Name == nameof(OnlineAiOptions.Id))
                Row(p, path, "createOnly", "avamedia_create_provider", "providerId", "稳定标识，变更通过删除/新建供应商。");
            else if (p.Name == nameof(OnlineAiOptions.EffectiveVisionModel)) Row(p, path, "metadata", null, "effectiveVisionModel");
            else if (typeof(McpProviderPatch).GetProperty(p.Name) is not null)
                Row(p, path, "readWrite", "avamedia_update_provider", "providerId + patch." + Camel(p.Name));
            else { unsupported.Add(path); Row(p, path, "unsupported", null, ""); }
        }
        rows.Add(new("wordLibraries.libraries", "WordLibrary[]", "readWrite", "avamedia_list_word_libraries", "avamedia_save_word_library", "libraryId + name + entries", "内置词库只读；自定义词库可保存/删除。"));
        rows.Add(new("wordLibraries.selections", "SelectedWord[]", "readWrite", "avamedia_get_word_preferences", "avamedia_set_word_preferences", "target + selection", "使用原生词库选择存储。"));
        rows.Add(new("wordLibraries.views", "WordLibraryView", "readWrite", "avamedia_get_word_preferences", "avamedia_set_word_preferences", "target + view / clearView", "target 为空表示词库管理视图。"));
        return new(rows.OrderBy(row => row.Path, StringComparer.Ordinal).ToArray(), unsupported.ToArray(), AIJsonUtilities.CreateJsonSchema(typeof(McpSettingsPatch)));
    }
}
