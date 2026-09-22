using System;
using System.Text.Json;
using RetryProxy.Core.Config;
using RetryProxy.Core.Internal;

namespace RetryProxy.Core.KeepAlive;

public enum KeepAliveFlavor
{
    Claude,
    Codex,
    Unknown,
}

public static class KeepAliveFlavorExtensions
{
    public static string Label(this KeepAliveFlavor flavor) => flavor switch
    {
        KeepAliveFlavor.Claude => "Claude Code",
        KeepAliveFlavor.Codex => "Codex",
        _ => "自动识别",
    };

    public static KeepAliveFlavor Detect(string path)
    {
        var trimmed = path.Split('?')[0].TrimEnd('/');
        if (trimmed.EndsWith("/messages", StringComparison.Ordinal))
        {
            return KeepAliveFlavor.Claude;
        }

        if (trimmed.EndsWith("/responses", StringComparison.Ordinal) || trimmed.EndsWith("/chat/completions", StringComparison.Ordinal))
        {
            return KeepAliveFlavor.Codex;
        }

        return KeepAliveFlavor.Unknown;
    }

    public static KeepAliveFlavor FromClientType(ClientType clientType) => clientType switch
    {
        ClientType.Claude => KeepAliveFlavor.Claude,
        _ => KeepAliveFlavor.Codex,
    };
}

/// <summary>最近一次成功请求的模板，保活探测据此补发。</summary>
public sealed class KeepAliveTemplate
{
    public KeepAliveTemplate(string method, string pathAndQuery, HeaderList headers, ReadOnlyMemory<byte> body)
    {
        Method = method;
        PathAndQuery = pathAndQuery;
        Headers = headers.Clone();
        Body = body;
        Flavor = KeepAliveFlavorExtensions.Detect(pathAndQuery);
    }

    public string Method { get; }

    public string PathAndQuery { get; }

    public HeaderList Headers { get; }

    public ReadOnlyMemory<byte> Body { get; }

    public KeepAliveFlavor Flavor { get; }

    public string? Model()
    {
        using var document = JsonText.TryParse(Body);
        return document?.RootElement.Get("model").AsString();
    }
}
