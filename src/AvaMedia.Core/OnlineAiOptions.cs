using System.Text.Json.Serialization;

namespace AvaMedia.Core;

public sealed class OnlineAiOptions
{
    public string Endpoint { get; set; } = "https://api.openai.com/v1";
    public string TextModel { get; set; } = "";
    public string VisionModel { get; set; } = "";
    [JsonIgnore] public string ApiKey { get; set; } = "";
    public bool UseJsonSchema { get; set; }
    [JsonIgnore] public string EffectiveVisionModel => string.IsNullOrWhiteSpace(VisionModel) ? TextModel : VisionModel;
    public OnlineAiOptions Clone() => (OnlineAiOptions)MemberwiseClone();

    public Uri CompletionUri()
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("线上 AI 地址须为 HTTPS，本机接口可使用 HTTP。地址不能包含凭据或查询参数。");
        var address = uri.AbsoluteUri.TrimEnd('/');
        return new(address.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? address : address + "/chat/completions");
    }

    public void Validate(bool requireModel = true)
    {
        _ = CompletionUri();
        if (TextModel.Length > 128 || VisionModel.Length > 128 || TextModel.Any(char.IsControl) || VisionModel.Any(char.IsControl))
            throw new ArgumentException("线上 AI 模型名称无效。");
        if (requireModel && string.IsNullOrWhiteSpace(TextModel))
            throw new ArgumentException("请在模型管理中配置线上 AI 的文本模型。");
        if (ApiKey.Length > 4096 || ApiKey.Any(char.IsWhiteSpace) || ApiKey.Any(char.IsControl)) throw new ArgumentException("API Key 无效。");
    }
}
