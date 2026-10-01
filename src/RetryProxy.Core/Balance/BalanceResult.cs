using RetryProxy.Core.Config;

namespace RetryProxy.Core.Balance;

/// <summary>单个 Key 的余额结果；外部文本在读取器内过滤，结果不携带密钥或原始响应。</summary>
public sealed record BalanceResult(
    decimal? Amount,
    string Unit,
    bool IsUnlimited,
    bool IsInvalid,
    BalanceQueryMode? DetectedMode,
    string? Error);
