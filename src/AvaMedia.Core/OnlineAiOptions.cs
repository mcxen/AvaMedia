using System.Text.Json.Serialization;

namespace AvaMedia.Core;

public enum OnlineAiTokenLimit { MaxCompletionTokens, MaxTokens }
public enum OnlineAiResponseFormat { Prompt, JsonObject, JsonSchema }

public sealed class OnlineAiOptions
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "OpenAI";
    public string Preset { get; set; } = "openai";
    public bool Enabled { get; set; } = true;
    public string Endpoint { get; set; } = "https://api.openai.com/v1";
    public string TextModel { get; set; } = "";
    public string VisionModel { get; set; } = "";
    [JsonIgnore] public string ApiKey { get; set; } = "";
    public OnlineAiTokenLimit TokenLimit { get; set; }
    public OnlineAiResponseFormat ResponseFormat { get; set; } = OnlineAiResponseFormat.JsonObject;
    public int TimeoutSeconds { get; set; } = 180;
    public string[] ModelIds { get; set; } = [];
    [JsonIgnore] public string EffectiveVisionModel => string.IsNullOrWhiteSpace(VisionModel) ? TextModel : VisionModel;
    public OnlineAiOptions Clone()
    { var copy = (OnlineAiOptions)MemberwiseClone(); copy.ModelIds = [.. ModelIds]; return copy; }

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

    public Uri ModelsUri()
    {
        var completion = CompletionUri().AbsoluteUri;
        return new(completion[..^"chat/completions".Length] + "models");
    }

    public void ValidateConnection()
    {
        Validate(requireModel: false); _ = CompletionUri();
        if (Preset is "openai" or "openrouter" or "siliconflow" && ApiKey.Length == 0)
            throw new ArgumentException("请填写供应商的 API Key。");
    }

    public void Validate(bool requireModel = true)
    {
        if (!Guid.TryParseExact(Id, "N", out _) || string.IsNullOrWhiteSpace(Name) || Name.Length > 80 || Name.Any(char.IsControl))
            throw new ArgumentException("供应商名称或标识无效。");
        if (Endpoint.Length > 2048) throw new ArgumentException("供应商接口地址过长。");
        if (requireModel || !string.IsNullOrWhiteSpace(Endpoint)) _ = CompletionUri();
        if (!Enum.IsDefined(TokenLimit) || !Enum.IsDefined(ResponseFormat) || TimeoutSeconds is < 10 or > 600)
            throw new ArgumentException("供应商高级参数无效，超时须为 10–600 秒。");
        if (TextModel.Length > 128 || VisionModel.Length > 128 || TextModel.Any(char.IsControl) || VisionModel.Any(char.IsControl))
            throw new ArgumentException("线上 AI 模型名称无效。");
        if (requireModel && string.IsNullOrWhiteSpace(TextModel))
            throw new ArgumentException("请在 AI 供应商中配置文本模型。");
        if (ModelIds.Length > 2048 || ModelIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(char.IsControl)))
            throw new ArgumentException("供应商模型列表无效。");
        if (ApiKey.Length > 4096 || ApiKey.Any(char.IsWhiteSpace) || ApiKey.Any(char.IsControl)) throw new ArgumentException("API Key 无效。");
    }
}
