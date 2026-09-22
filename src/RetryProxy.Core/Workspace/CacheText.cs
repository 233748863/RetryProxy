using System;
using RetryProxy.Core.Metrics;

namespace RetryProxy.Core.Workspace;

/// <summary>缓存面板的文案函数（对应 ui/cache.rs 里与绘制无关的部分）。</summary>
public static class CacheText
{
    public const string RateHelp = "命中率 = 从缓存读取的输入量 ÷ 总输入量，按输入量计算，不按请求次数平均。\n总输入按回复格式统一计算；缓存写入单独展示，只有缓存读取计为命中。\n仅统计本通道当天成功完成且用量有效的请求；按本机日期切换，重启后从日志恢复当天数据。";

    public const string UsageHelp = "token = 模型计算输入用量的单位。\n未获取、无输入和用量异常的请求不计入命中率，也不算完全未命中。";

    public static string RateText(double? rate) => rate switch
    {
        > 0.0 and < 0.01 => "<0.01%",
        > 99.99 and < 100.0 => ">99.99%",
        { } value => value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "%",
        null => "暂无数据",
    };

    public static string CountText(ulong value) => value switch
    {
        <= 9_999 => value.ToString(),
        <= 99_999_999 => (value / 10_000.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "万",
        <= 999_999_999_999 => (value / 100_000_000.0).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "亿",
        _ => (value / 1_000_000_000_000.0).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "万亿",
    };

    public static string OptionalCount(ulong? value) => value?.ToString() ?? "未获取";

    public static string RequestRateText(CacheRequest request)
    {
        if (request.HitRatePercent() is { } rate)
        {
            return RateText(rate);
        }

        if (request.UsageIsInvalid())
        {
            return "用量异常";
        }

        return request.TotalInputTokens() == 0 && request.CachedTokens == 0 ? "无输入" : "未获取";
    }

    public static string RequestTime(CacheRequest request)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(request.CompletedAtUnixMs).ToLocalTime().ToString("HH:mm:ss");
        }
        catch (ArgumentOutOfRangeException)
        {
            return "--:--:--";
        }
    }

    public static string RequestHint(CacheRequest request)
    {
        return $"{RequestTime(request)} · {request.RequestId} · {request.Model}\n"
            + $"命中 {RequestRateText(request)} · 读取 {OptionalCount(request.CachedTokens)} / 总输入 {OptionalCount(request.TotalInputTokens())} token\n"
            + $"缓存写入 {OptionalCount(request.CacheCreationTokens)} token\n"
            + $"缓存标识：{request.CacheKeyStatus}\n{UsageHelp}";
    }

    /// <summary>最近一次成功请求的第二行说明。</summary>
    public static string LatestDetail(CacheRequest? request)
    {
        if (request is null)
        {
            return "收到回复后更新";
        }

        return request.Usage() is { } usage
            ? $"读取 {CountText(usage.Cached)} / 总输入 {CountText(usage.Input)}"
            : "用量未计入累计命中率";
    }

    public static string CreationTotalText(CacheSnapshot cache)
    {
        if (cache.CacheCreationMeasuredRequests == 0)
        {
            return "未获取";
        }

        return cache.CacheCreationMeasuredRequests < cache.MeasuredRequests
            ? $"{CountText(cache.CacheCreationTokens)}（部分）"
            : CountText(cache.CacheCreationTokens);
    }

    public static string CreationHelp(CacheSnapshot cache)
    {
        return "缓存写入 = 本次回复报告的新建缓存用量，不计为缓存命中。\n"
            + $"{cache.CacheCreationMeasuredRequests} / {cache.MeasuredRequests} 个有效请求取得写入用量，合计 {cache.CacheCreationTokens} token；未获取的写入用量不填成 0。";
    }
}
