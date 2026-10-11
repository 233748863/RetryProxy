using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using RetryProxy.Core.Config;
using RetryProxy.Core.Metrics;
using RetryProxy.Core.Storage;
using Xunit;

namespace RetryProxy.Tests;

public sealed class ConversationReportTests
{
    private static readonly DateOnly Today = new(2026, 10, 11);
    private static StoredUsageRecord Row(string id, string? session, int day = 0, ClientType client = ClientType.Codex) => new(client.ToString(), Today.AddDays(day), new()
    {
        RequestId = id, Outcome = RequestOutcome.Success,
        UpdatedAtUnixMs = new DateTimeOffset(Today.AddDays(day).ToDateTime(new TimeOnly(12, 0), DateTimeKind.Local)).ToUnixTimeMilliseconds(),
        Attribution = new RequestAttribution { Client = client, ProviderId = "p", Model = "test" },
        Usage = new RequestUsage { InputTokens = 100, CachedTokens = 0, OutputTokens = 10, InputAccounting = CacheInputAccounting.IncludesCached, Model = "test" },
        PromptAnalysis = new PromptAnalysis { SessionHash = session, Parts = [new(PromptPartKind.Conversation, 10)] },
    });

    [Fact]
    public void GroupingRespectsClientsPeriodsUnknownIdsAndHistoricalDuplicates()
    {
        var hash = new string('a', 64);
        var rows = new[] { Row("a", hash), Row("b", hash), Row("old", hash, -1), Row("c", hash, client: ClientType.Claude),
            Row("a", hash, -1), Row("unknown", null), Row("broken", "invalid"), Row(ProxyMetrics.KeepAlivePrefix + "bg", hash) };
        var prices = new ModelPricing(overrides: [new() { Model = "test", Price = new() { Rates = new() { Input = 1, Output = 2, CacheRead = 0 } } }]);
        var report = ConversationReport.Build(rows, new Dictionary<string, ClientType>(), prices, Today, Today, "");
        Assert.Equal(2, report.Conversations);
        Assert.Equal(2, report.Unassigned);
        Assert.Equal(5, report.Totals.Requests);
        Assert.Equal(0.0006m, report.Totals.Cost);
        Assert.Equal(2, report.Groups.Single(g => g.Client == "Codex").Requests.Count);
        Assert.True(report.Groups.Last().IsUnassigned);
        Assert.Equal(4, ConversationReport.Build(rows, new Dictionary<string, ClientType>(), prices, Today, Today, "Codex").Totals.Requests);
    }

    [Fact]
    public void AnalysisRoundTripsInExistingSqliteRecordsAndMissingPricesStayUnknown()
    {
        var directory = Path.Combine(Path.GetTempPath(), "retry-conversation-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var data = new DataDatabase(directory);
            var row = Row("request", new string('b', 64));
            var storage = new SqliteDailyStorage(data, "route", "Codex");
            storage.Open(Today, false).Journal!.Append(row.Request);
            var read = new UsageHistory().Read(data);
            var record = Assert.Single(read.Records);
            Assert.Equal(row.Request.PromptAnalysis!.SessionHash, record.Request.PromptAnalysis!.SessionHash);
            Assert.Equal(10, record.Request.PromptAnalysis.EstimatedTokens);
            var report = ConversationReport.Build(read.Records, new Dictionary<string, ClientType>(), new ModelPricing(), null, Today, "");
            Assert.Equal(1, report.Totals.Unpriced);
            Assert.Equal(1, report.Conversations);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
