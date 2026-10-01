using System;
using RetryProxy.Core.Config;

namespace RetryProxy.Core.Balance;

public readonly record struct BalanceKey(string ProviderId, string KeyId);

/// <summary>一轮余额查询的输入快照；密钥只供请求使用，调试输出不包含凭据。</summary>
public sealed class BalanceRequest
{
    public required BalanceKey Key { get; init; }
    public required ClientType ClientType { get; init; }
    public required string BaseUrl { get; init; }
    public required string ApiKey { get; init; }
    public required BalanceQuery Query { get; init; }

    // Detected 仅优化下一次请求；后台记住接口不使刚取到的余额失效。
    internal bool SameSourceAs(BalanceRequest other) => Key == other.Key && ClientType == other.ClientType
        && BaseUrl == other.BaseUrl && ApiKey == other.ApiKey && Query.Mode == other.Query.Mode;

    public override string ToString() => $"BalanceRequest {{ {ClientType}, {Key.ProviderId}, {Key.KeyId} }}";
}

/// <summary>仅用于展示的余额快照，不包含上游地址或密钥。</summary>
public sealed record BalanceSnapshot(BalanceResult? Result, DateTimeOffset? CheckedAt, bool IsRefreshing, bool IsEnabled,
    string? PersistenceError = null)
{
    public static BalanceSnapshot Empty { get; } = new(null, null, false, false);
}
