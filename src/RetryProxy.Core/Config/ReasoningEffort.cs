using System;
using System.Collections.Generic;
using System.Linq;

namespace RetryProxy.Core.Config;

/// <summary>后台问答的思考强度；Default 不覆盖本机客户端设置。</summary>
public enum ReasoningEffort
{
    Default,
    Low,
    Medium,
    High,
    XHigh,
    Max,
    Ultra,
}

public static class ReasoningEffortExtensions
{
    public static string AsStr(this ReasoningEffort effort) => effort switch
    {
        ReasoningEffort.Default => "default",
        ReasoningEffort.Low => "low",
        ReasoningEffort.Medium => "medium",
        ReasoningEffort.High => "high",
        ReasoningEffort.XHigh => "xhigh",
        ReasoningEffort.Max => "max",
        ReasoningEffort.Ultra => "ultra",
        _ => throw new ArgumentOutOfRangeException(nameof(effort)),
    };

    public static string Label(this ReasoningEffort effort) => effort switch
    {
        ReasoningEffort.Default => "默认（沿用客户端）",
        ReasoningEffort.Low => "低 · low",
        ReasoningEffort.Medium => "中 · medium",
        ReasoningEffort.High => "高 · high",
        ReasoningEffort.XHigh => "超高 · xhigh",
        ReasoningEffort.Max => "最高 · max",
        ReasoningEffort.Ultra => "极限 · ultra",
        _ => throw new ArgumentOutOfRangeException(nameof(effort)),
    };

    public static ReasoningEffort Parse(string value) => value switch
    {
        "default" => ReasoningEffort.Default,
        "low" => ReasoningEffort.Low,
        "medium" => ReasoningEffort.Medium,
        "high" => ReasoningEffort.High,
        "xhigh" => ReasoningEffort.XHigh,
        "max" => ReasoningEffort.Max,
        "ultra" => ReasoningEffort.Ultra,
        _ => throw new ConfigException("思考强度必须为 default、low、medium、high、xhigh、max 或 ultra"),
    };

    // 客户端提供的档位集合；具体模型或供应商仍可能只支持其中一部分。
    public static bool IsSupportedBy(this ReasoningEffort effort, ClientType clientType) =>
        Enum.IsDefined(effort) && Enum.IsDefined(clientType)
        && (clientType == ClientType.Codex || effort != ReasoningEffort.Ultra);

    public static IReadOnlyList<ReasoningEffort> AvailableFor(ClientType clientType) =>
        Enum.GetValues<ReasoningEffort>().Where(effort => effort.IsSupportedBy(clientType)).ToArray();
}
