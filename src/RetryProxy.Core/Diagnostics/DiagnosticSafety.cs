using System;
using System.Text;
using RetryProxy.Core.Config;
using RetryProxy.Core.Stats;

namespace RetryProxy.Core.Diagnostics;

/// <summary>在进入诊断队列前只复制安全字段；调用方不得传入报文或原始异常文本。</summary>
public static class DiagnosticSafety
{
    private const string Hidden = "[已隐藏]";
    private static readonly string[] Endpoints =
    [
        "/messages/count_tokens", "/chat/completions", "/responses", "/messages",
        "/completions", "/embeddings", "/models",
    ];

    public static DiagnosticRequestInfo Sanitize(DiagnosticRequestInfo request) => request with
    {
        RequestId = Identifier(request.RequestId) ?? string.Empty,
        Method = request.Method.ToUpperInvariant() is "GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS"
            ? request.Method.ToUpperInvariant() : "OTHER",
        Endpoint = Endpoint(request.Endpoint),
    };

    public static DiagnosticEntry Sanitize(DiagnosticEntry entry) => entry with
    {
        ElapsedSeconds = Seconds(entry.ElapsedSeconds) ?? 0,
        Target = entry.Target is { } target ? target with
        {
            ProviderId = Identifier(target.ProviderId) ?? string.Empty,
            ProviderName = Text(target.ProviderName, 64) ?? string.Empty,
            KeyId = Identifier(target.KeyId) ?? string.Empty,
            KeyName = Text(target.KeyName, 64) ?? string.Empty,
            Model = Text(target.Model, 128),
        } : null,
        StatusCode = entry.StatusCode is >= 100 and <= 599 ? entry.StatusCode : null,
        Reason = Text(entry.Reason, 160),
        ErrorCode = Identifier(entry.ErrorCode),
        UpstreamRequestId = Identifier(entry.UpstreamRequestId),
        LastEvent = Identifier(entry.LastEvent),
        DurationSeconds = Seconds(entry.DurationSeconds),
        PlannedWaitSeconds = Seconds(entry.PlannedWaitSeconds),
        FirstContentSeconds = Seconds(entry.FirstContentSeconds),
    };

    internal static DiagnosticTarget Target(ChannelSnapshot snapshot, string? model)
    {
        // 在快照仍由管线持有时去掉已知凭据；诊断对象不会引用快照或这些凭据。
        string? Clean(string? value, int length) => Text(Redact(value, snapshot.ApiKey, snapshot.LocalToken), length);
        return new DiagnosticTarget(
            Identifier(Redact(snapshot.ProviderId, snapshot.ApiKey, snapshot.LocalToken)) ?? string.Empty,
            Clean(snapshot.ProviderName, 64) ?? string.Empty,
            Identifier(Redact(snapshot.KeyId, snapshot.ApiKey, snapshot.LocalToken)) ?? string.Empty,
            Clean(snapshot.KeyName, 64) ?? string.Empty,
            Clean(model, 128));
    }

    public static string Endpoint(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "/其他接口";
        var question = path.IndexOf('?');
        var value = (question >= 0 ? path[..question] : path).TrimEnd('/');
        foreach (var endpoint in Endpoints)
        {
            if (value.EndsWith(endpoint, StringComparison.Ordinal)) return endpoint;
        }
        return "/其他接口";
    }

    public static string? Identifier(string? value) => DiagnosticText.CleanDiagnosticIdentifier(value);

    public static string? Text(string? value, int maximumCharacters = 128)
    {
        if (string.IsNullOrEmpty(value)) return null;
        // 不允许常见凭据形态或 URL 借名称/错误摘要进入诊断文件。
        if (value.Contains("sk-", StringComparison.OrdinalIgnoreCase)
            || value.Contains("sess-", StringComparison.OrdinalIgnoreCase)
            || value.Contains("eyj", StringComparison.OrdinalIgnoreCase)
            || value.Contains("://", StringComparison.Ordinal)) return Hidden;
        var result = new StringBuilder();
        var count = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (DiagnosticText.IsControl(rune)) continue;
            if (count++ >= maximumCharacters) break;
            result.Append(rune.ToString());
        }
        var cleaned = result.ToString().Trim();
        return cleaned.Length > 0 ? cleaned : null;
    }

    internal static string? Redact(string? value, params string[] secrets)
    {
        if (value is null) return null;
        foreach (var secret in secrets)
        {
            if (!string.IsNullOrEmpty(secret)) value = value.Replace(secret, Hidden, StringComparison.Ordinal);
        }
        return value;
    }

    private static double? Seconds(double? value) => value is { } seconds && double.IsFinite(seconds) && seconds >= 0
        ? seconds : null;
}
