namespace AvaMedia.Core;

/// <summary>Resolve mirrors at each retry round without changing artifact identities or hashes.</summary>
internal static class ModelDownloadSources
{
    private static readonly string[] BuiltInMirrors = ["https://hf-mirror.com", "https://hf-cf.northstar.cool"];

    public static string[] Resolve(ModelArtifact artifact)
    {
        var hubSources = artifact.Sources.Where(source => new Uri(source).Host.Equals("huggingface.co", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (hubSources.Length == 0) return artifact.Sources.Distinct(StringComparer.Ordinal).ToArray();
        var endpoint = Environment.GetEnvironmentVariable("HF_ENDPOINT")?.Trim();
        var extra = Environment.GetEnvironmentVariable("AVAMEDIA_HF_MIRRORS") ?? "";
        var mirrors = (string.IsNullOrEmpty(endpoint) ? [] : new[] { endpoint })
            .Concat(extra.Split([';', ',', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Concat(BuiltInMirrors).Select(ParseEndpoint).DistinctBy(uri => uri.AbsoluteUri.TrimEnd('/')).ToArray();
        var mirrored = mirrors.SelectMany(mirror => hubSources.Select(source => mirror.AbsoluteUri.TrimEnd('/') + new Uri(source).PathAndQuery));
        var sources = new List<string>();
        if (string.IsNullOrEmpty(endpoint) && string.IsNullOrWhiteSpace(extra)) sources.AddRange(hubSources);
        sources.AddRange(mirrored); sources.AddRange(artifact.Sources);
        return sources.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static Uri ParseEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length > 0
            || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0)
            throw new ArgumentException("模型镜像须为 HTTP 或 HTTPS 地址，不能包含凭据、查询参数或片段。");
        return endpoint;
    }
}
