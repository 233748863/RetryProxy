using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace RetryProxy.Core.Metrics;

public static class UsageCsv
{
    /// <summary>导出当前筛选的所有页；缺失数值为空，显示名称防止电子表格公式注入。</summary>
    public static void Write(TextWriter writer, IEnumerable<UsageRecord> records)
    {
        WriteRow(writer, new[] { "日期", "结束时间", "客户端", "供应商", "Key 名称", "模型", "结果", "重试次数",
            "普通输入", "缓存读取", "缓存写入", "输出", "思考（包含在输出中）", "已知总用量", "用量完整",
            "缓存命中率(%)", "命中率为上限", "估算费用(USD)", "费用为下限", "首内容等待(秒)", "总耗时(秒)", "请求编号" });
        foreach (var r in records)
        {
            var t = r.Tokens;
            WriteRow(writer, new[]
            {
                r.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), r.CompletedAt?.ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty,
                r.Group(UsageDimension.Client).Name, r.Group(UsageDimension.Provider).Name, r.KeyName, r.Group(UsageDimension.Model).Name,
                r.Outcome == RequestOutcome.Success ? "成功" : "失败", r.Retries.ToString(CultureInfo.InvariantCulture),
                Number(t?.Input), Number(t?.CacheRead), Number(t?.CacheWrite), Number(t?.Output), Number(t?.Reasoning),
                t is { HasUsage: true } ? Number(t.KnownTokens) : string.Empty, t?.Complete == true ? "是" : "否",
                t?.HitRate?.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty, t?.PromptIsLowerBound == true ? "是" : "否",
                Number(r.Quote.Amount), r.Quote.IsLowerBound ? "是" : "否",
                r.Usage?.FirstContentSeconds?.ToString("0.######", CultureInfo.InvariantCulture) ?? string.Empty,
                r.Usage?.DurationSeconds?.ToString("0.######", CultureInfo.InvariantCulture) ?? string.Empty, r.RequestId,
            });
        }
    }

    private static string Number(decimal? value) => value?.ToString("0.############################", CultureInfo.InvariantCulture) ?? string.Empty;

    private static void WriteRow(TextWriter writer, IEnumerable<string> cells) => writer.WriteLine(string.Join(",", cells.Select(Escape)));

    private static string Escape(string value)
    {
        var trimmed = value.AsSpan().TrimStart();
        if (trimmed.Length > 0 && trimmed[0] is '=' or '+' or '-' or '@') value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
