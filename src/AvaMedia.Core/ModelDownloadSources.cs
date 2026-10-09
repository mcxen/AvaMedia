namespace AvaMedia.Core;

/// <summary>User-selectable model download source. Auto orders the built-in sources by region.</summary>
public enum ModelSourceKind { Auto, HuggingFace, ModelScope, HfMirror, Custom }
public sealed record ModelSourcePreference(ModelSourceKind Kind = ModelSourceKind.Auto, string CustomUrl = "")
{
    public static ModelSourcePreference From(AppSettings settings)
        => new(Enum.TryParse<ModelSourceKind>(settings.ModelSource, true, out var kind) ? kind : ModelSourceKind.Auto, settings.ModelSourceUrl ?? "");
    public void Validate()
    {
        if (Kind != ModelSourceKind.Custom) return;
        if (string.IsNullOrWhiteSpace(CustomUrl)) throw new ArgumentException("请填写自定义模型下载源地址。");
        _ = ModelDownloadSources.ParseEndpoint(CustomUrl.Trim());
    }
}

/// <summary>Resolve sources at each retry round without changing artifact identities or hashes.</summary>
public static class ModelDownloadSources
{
    public const string HuggingFaceHost = "huggingface.co";
    public const string ModelScopeHost = "modelscope.cn";
    public const string HfMirrorEndpoint = "https://hf-mirror.com";
    private static ModelSourcePreference _preference = new();
    /// <summary>Set by the app from AppSettings at startup and whenever settings are applied.</summary>
    public static ModelSourcePreference Preference { get => Volatile.Read(ref _preference); set => Volatile.Write(ref _preference, value ?? new()); }

    /// <summary>Auto prefers ModelScope, then HF-Mirror, then Hugging Face for zh-CN / China time zones; otherwise Hugging Face first.</summary>
    public static bool PrefersChinaSources()
    {
        var culture = System.Globalization.CultureInfo.CurrentUICulture.Name;
        var zone = TimeZoneInfo.Local.Id;
        return culture.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) || culture.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase)
            || zone is "Asia/Shanghai" or "Asia/Chongqing" or "Asia/Chungking" or "Asia/Harbin" or "Asia/Urumqi" or "PRC" or "China Standard Time";
    }

    public static string[] Resolve(ModelArtifact artifact) => Resolve(artifact, Preference, PrefersChinaSources());

    public static string[] Resolve(ModelArtifact artifact, ModelSourcePreference preference, bool china)
    {
        var hubSources = artifact.Sources.Where(source => IsHost(source, HuggingFaceHost)).ToArray();
        var scopeSources = artifact.Sources.Where(source => IsHost(source, ModelScopeHost)).ToArray();
        if (hubSources.Length == 0 && scopeSources.Length == 0) return artifact.Sources.Distinct(StringComparer.Ordinal).ToArray();
        IEnumerable<string> Mirror(string endpoint) => hubSources.Select(source => ParseEndpoint(endpoint).AbsoluteUri.TrimEnd('/') + new Uri(source).PathAndQuery);
        IEnumerable<string> Kind(ModelSourceKind kind) => kind switch
        {
            ModelSourceKind.HuggingFace => hubSources,
            ModelSourceKind.ModelScope => scopeSources,
            ModelSourceKind.HfMirror => Mirror(HfMirrorEndpoint),
            ModelSourceKind.Custom => string.IsNullOrWhiteSpace(preference.CustomUrl) ? [] : Mirror(preference.CustomUrl.Trim()),
            _ => []
        };
        // Environment overrides keep working and are tried before the selected source.
        var endpoint = Environment.GetEnvironmentVariable("HF_ENDPOINT")?.Trim();
        var extra = Environment.GetEnvironmentVariable("AVAMEDIA_HF_MIRRORS") ?? "";
        var environment = (string.IsNullOrEmpty(endpoint) ? [] : new[] { endpoint })
            .Concat(extra.Split([';', ',', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Select(ParseEndpoint).DistinctBy(uri => uri.AbsoluteUri.TrimEnd('/')).ToArray();
        ModelSourceKind[] automatic = china
            ? [ModelSourceKind.ModelScope, ModelSourceKind.HfMirror, ModelSourceKind.HuggingFace]
            : [ModelSourceKind.HuggingFace, ModelSourceKind.ModelScope, ModelSourceKind.HfMirror];
        var sources = new List<string>();
        sources.AddRange(environment.SelectMany(mirror => Mirror(mirror.AbsoluteUri)));
        // An explicit choice goes first; the other sources remain fallbacks, e.g. for models it does not host.
        if (preference.Kind != ModelSourceKind.Auto) sources.AddRange(Kind(preference.Kind));
        foreach (var kind in automatic) sources.AddRange(Kind(kind));
        sources.AddRange(artifact.Sources);
        return sources.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Human-readable source name for progress text.</summary>
    public static string Describe(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) return source;
        var host = uri.Host;
        if (IsHost(source, HuggingFaceHost)) return "Hugging Face";
        if (IsHost(source, ModelScopeHost)) return "ModelScope";
        if (host.Equals(new Uri(HfMirrorEndpoint).Host, StringComparison.OrdinalIgnoreCase)) return "HF-Mirror";
        if (host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)) return "GitHub";
        return host;
    }

    private static bool IsHost(string source, string host)
    {
        var actual = new Uri(source).Host;
        return actual.Equals(host, StringComparison.OrdinalIgnoreCase) || actual.EndsWith("." + host, StringComparison.OrdinalIgnoreCase);
    }

    internal static Uri ParseEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length > 0
            || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0)
            throw new ArgumentException("模型镜像须为 HTTP 或 HTTPS 地址，不能包含凭据、查询参数或片段。");
        return endpoint;
    }
}
