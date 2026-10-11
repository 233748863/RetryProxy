using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RetryProxy.Core.Config;

namespace RetryProxy.Core.Metrics;

public sealed record ConversationRequest(UsageRecord Usage, PromptAnalysis? Analysis)
{
    public string Identity => Usage.RouteId + "\n" + Usage.RequestId;
}

public sealed record ConversationGroup(string Identity, string Client, string? Hash, IReadOnlyList<ConversationRequest> Requests, UsageTotals Totals)
{
    public bool IsUnassigned => Hash is null;
    public DateTime? Last => Requests.FirstOrDefault()?.Usage.CompletedAt;
}

public sealed record ConversationReport(IReadOnlyList<ConversationGroup> Groups, UsageTotals Totals, long Conversations, long Unassigned)
{
    /// <summary>复用历史读取层的跨日去重。缺失标识是单独的收纳项，不虚构会话身份。</summary>
    public static ConversationReport Build(IEnumerable<StoredUsageRecord> history, IReadOnlyDictionary<string, ClientType> clients,
        ModelPricing prices, DateOnly? first, DateOnly last, string client, CancellationToken token = default)
    {
        var groups = new Dictionary<string, List<ConversationRequest>>(StringComparer.Ordinal);
        var latest = new Dictionary<(string Route, string Request), StoredUsageRecord>();
        foreach (var row in history)
        {
            token.ThrowIfCancellationRequested();
            if (row.Request.Outcome is null || row.Request.RequestId.StartsWith(ProxyMetrics.KeepAlivePrefix, StringComparison.Ordinal)) continue;
            var key = (row.RouteId, row.Request.RequestId);
            if (!latest.TryGetValue(key, out var previous) || row.Request.UpdatedAtUnixMs > previous.Request.UpdatedAtUnixMs
                || row.Request.UpdatedAtUnixMs == previous.Request.UpdatedAtUnixMs && row.Date > previous.Date) latest[key] = row;
        }
        var totals = new UsageTotals();
        long unassigned = 0;
        foreach (var stored in latest.Values)
        {
            token.ThrowIfCancellationRequested();
            if (stored.Request.Outcome is null || stored.Date > last || first is { } start && stored.Date < start) continue;
            var row = UsageRecord.From(stored, clients, prices);
            if (client.Length > 0 && row.Client != client) continue;
            var analysis = stored.Request.PromptAnalysis;
            var hash = analysis?.SessionHash;
            // 损坏或外部编辑的摘要不用于合并两条请求。
            if (hash is not { Length: 64 } || !hash.All(char.IsAsciiHexDigit)) hash = null;
            if (analysis is not null && analysis.SessionHash != hash) analysis = analysis with { SessionHash = hash };
            var key = hash is null ? "unassigned" : row.Client + "\n" + hash;
            if (!groups.TryGetValue(key, out var requests)) groups[key] = requests = [];
            requests.Add(new(row, analysis));
            totals.Add(row);
            if (hash is null) unassigned++;
        }
        var result = new List<ConversationGroup>();
        foreach (var pair in groups)
        {
            token.ThrowIfCancellationRequested();
            var ordered = pair.Value.OrderByDescending(r => r.Usage.CompletedAt).ThenBy(r => r.Identity, StringComparer.Ordinal).ToArray();
            var groupTotals = new UsageTotals();
            foreach (var row in ordered) groupTotals.Add(row.Usage);
            result.Add(new(pair.Key, pair.Key == "unassigned" ? string.Empty : ordered[0].Usage.Client,
                pair.Key == "unassigned" ? null : ordered[0].Analysis!.SessionHash, ordered, groupTotals));
        }
        return new(result.OrderBy(g => g.IsUnassigned).ThenByDescending(g => g.Last).ThenBy(g => g.Identity, StringComparer.Ordinal).ToArray(),
            totals, result.LongCount(g => !g.IsUnassigned), unassigned);
    }
}
