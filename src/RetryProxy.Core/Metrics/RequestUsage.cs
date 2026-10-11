using System;
using System.Text.Json.Serialization;
using RetryProxy.Core.Config;

namespace RetryProxy.Core.Metrics;

/// <summary>供历史分析使用的元数据；不含凭据、正文或会话标识。</summary>
public sealed record RequestAttribution
{
    public string ProviderId { get; init; } = string.Empty;
    public string ProviderName { get; init; } = string.Empty;
    public string KeyId { get; init; } = string.Empty;
    public string KeyName { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public ClientType? Client { get; init; }
}

/// <summary>
/// 上游实际报告的用量和时间。可选字段保持 null；思考是输出的子集。
/// 单独写在 daily_requests.record_json 中，不改变现有 health 的缓存快照格式。
/// </summary>
public sealed record RequestUsage
{
    public string Model { get; init; } = string.Empty;
    public CacheInputAccounting InputAccounting { get; init; }
    public ulong? InputTokens { get; init; }
    public ulong? OutputTokens { get; init; }
    public ulong? CachedTokens { get; init; }
    public ulong? CacheCreationTokens { get; init; }
    public ulong? CacheCreation1hTokens { get; init; }
    public ulong? ReasoningTokens { get; init; }
    public bool IsStreaming { get; init; }
    public double? FirstContentSeconds { get; init; }
    public double? FirstTextSeconds { get; init; }
    public double? DurationSeconds { get; init; }

    public static RequestUsage? FromLegacy(CacheRequest? cache) => cache is null ? null : new()
    {
        Model = cache.Model,
        InputAccounting = cache.InputAccounting,
        InputTokens = cache.InputTokens,
        CachedTokens = cache.CachedTokens,
        CacheCreationTokens = cache.CacheCreationTokens,
    };

    /// <summary>把两种协议统一为互不重叠的输入、读取、写入、输出。</summary>
    public NormalizedUsage Normalize()
    {
        decimal? input = InputTokens;
        decimal? read = CachedTokens;
        decimal? write = CacheCreationTokens;
        decimal? output = OutputTokens;
        decimal? prompt;
        var invalid = false;
        if (InputAccounting == CacheInputAccounting.IncludesCached)
        {
            prompt = input;
            if (input is not null && read is not null)
            {
                input -= read + (write ?? 0);
                if (input < 0)
                {
                    invalid = true;
                    input = read = write = null;
                }
            }
            else input = null;
        }
        else
        {
            prompt = input is not null && read is not null ? input + read + (write ?? 0) : null;
        }

        decimal? reasoning = ReasoningTokens;
        if (reasoning is not null && (output is null || reasoning > output)) reasoning = null;
        var lowerBound = InputAccounting == CacheInputAccounting.ExcludesCached && write is null;
        var complete = !invalid && prompt is not null && output is not null && !lowerBound;
        var known = (prompt ?? input + (read ?? 0) + (write ?? 0)) ?? (read ?? 0) + (write ?? 0);
        known += output ?? 0;
        return new NormalizedUsage(input, output, read, write, reasoning, prompt, known,
            complete, lowerBound, invalid, InputTokens is not null || CachedTokens is not null || CacheCreationTokens is not null || OutputTokens is not null);
    }

    /// <summary>同 magpie，按输出量 / 各回复生成时间之和计算，禁止平均每个请求的速率。</summary>
    public (decimal Tokens, double Seconds)? Decode()
    {
        if (!IsStreaming || OutputTokens is not { } output || FirstContentSeconds is not { } first
            || DurationSeconds is not { } duration || !double.IsFinite(first) || !double.IsFinite(duration) || first <= 0)
            return null;
        var reasoning = ReasoningTokens ?? 0;
        if (reasoning > output) return null;
        var start = reasoning > 0 ? FirstTextSeconds : first;
        if (start is not { } at || !double.IsFinite(at) || at < first || at >= duration) return null;
        var tokens = output - reasoning;
        var seconds = duration - at;
        // 短暂或整包吐出的响应不能证明生成速度；与参考项目 DecodeWindow 的下限一致。
        if (tokens == 0 || seconds < 0.1 || tokens / seconds > 10_000) return null;
        if (reasoning > 0 && (double)tokens * (at - first) > 20 * (double)reasoning * seconds) return null;
        return (tokens, seconds);
    }
}

public sealed record NormalizedUsage(
    decimal? Input, decimal? Output, decimal? CacheRead, decimal? CacheWrite, decimal? Reasoning,
    decimal? Prompt, decimal KnownTokens, bool Complete, bool PromptIsLowerBound, bool Invalid, bool HasUsage)
{
    public double? HitRate => !Invalid && Prompt is > 0 && CacheRead is { } read ? (double)(100 * read / Prompt.Value) : null;
}
