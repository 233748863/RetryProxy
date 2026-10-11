using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetryProxy.Core.Config;
using RetryProxy.Core.Metrics;
using RetryProxy.Core.Storage;
using Xunit;

namespace RetryProxy.Tests;

public sealed class UsageReportTests
{
    private static readonly DateOnly Today = new(2026, 10, 11);
    private static readonly IReadOnlyDictionary<string, ClientType> Routes = new Dictionary<string, ClientType> { ["r"] = ClientType.Codex };
    private static readonly ModelPrice Price = new()
    {
        Rates = new TokenPrices { Input = 2, Output = 10, CacheRead = 0.2m, CacheWrite = 2.5m, CacheWrite1h = 4 },
    };

    private static ModelPricing Prices() => new(overrides: new[] { new ModelPriceOverride { Model = "test-model", Price = Price } });

    private static RequestUsage Usage(ulong input = 1000, ulong read = 800, ulong output = 100) => new()
    {
        Model = "test-model", InputAccounting = CacheInputAccounting.IncludesCached,
        InputTokens = input, CachedTokens = read, OutputTokens = output,
    };

    private static StoredUsageRecord Row(string id, DateOnly? date = null, RequestUsage? usage = null, string provider = "p", RequestOutcome outcome = RequestOutcome.Success)
    {
        var day = date ?? Today;
        return new("r", day, new DailyRequest
        {
            RequestId = id, Outcome = outcome,
            UpdatedAtUnixMs = new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 30), DateTimeKind.Local)).ToUnixTimeMilliseconds(),
            Usage = usage ?? Usage(),
            Attribution = new RequestAttribution { ProviderId = provider, ProviderName = provider, Model = "test-model", Client = ClientType.Codex },
        });
    }

    private static UsageReport Report(IEnumerable<StoredUsageRecord> rows, UsageQuery? query = null) =>
        UsageReport.Build(rows.ToList(), Routes, Prices(), query ?? new UsageQuery { FirstDate = Today, LastDate = Today, Today = Today });

    [Fact]
    public void BothProtocolsNormalizeToTheSameDisjointTokensAndDoNotAddReasoningAgain()
    {
        var openai = Usage() with { InputTokens = 1000, CachedTokens = 800, CacheCreationTokens = 100, OutputTokens = 50, ReasoningTokens = 20 };
        var claude = openai with { InputAccounting = CacheInputAccounting.ExcludesCached, InputTokens = 100 };
        Assert.Equal(openai.Normalize(), claude.Normalize());
        Assert.Equal(1050m, openai.Normalize().KnownTokens);
        Assert.Equal(100m, openai.Normalize().Input);
        Assert.Equal(80, openai.Normalize().HitRate);
    }

    [Fact]
    public void LegacyRecordsKeepMissingOutputAndCostsUnknown()
    {
        var row = Row("old");
        row.Request.Usage = null;
        row.Request.Attribution = null;
        row.Request.Cache = new CacheRequest { Model = "test-model", InputTokens = 1000, CachedTokens = 800 };
        var report = Report(new[] { row });
        Assert.Equal(1000m, report.Totals.Tokens);
        Assert.Equal(1, report.Totals.IncompleteUsage);
        Assert.Equal(0, report.Totals.OutputReported);
        Assert.Equal(1, report.Totals.Unpriced);
        Assert.Equal(UsageRecord.Unknown, report.Records[0].ProviderId);
        Assert.Equal("Codex", report.Records[0].Client);
    }

    [Fact]
    public void MissingClaudeWritesAreAnExplicitLowerBoundAndRateUpperBound()
    {
        var usage = Usage(100, 800, 50) with { InputAccounting = CacheInputAccounting.ExcludesCached };
        var n = usage.Normalize();
        Assert.True(n.PromptIsLowerBound);
        Assert.False(n.Complete);
        Assert.Null(n.CacheWrite);
        Assert.Equal(950m, n.KnownTokens);
        var quote = Price.Quote(usage, "test");
        Assert.True(quote.IsLowerBound);
        Assert.Equal(0.00086m, quote.Amount);
    }

    [Fact]
    public void MissingCacheReadDoesNotHideKnownOpenAiTotalButCannotBePriced()
    {
        var usage = Usage() with { CachedTokens = null };
        Assert.Equal(1100m, usage.Normalize().KnownTokens);
        Assert.Null(usage.Normalize().Input);
        Assert.Null(Price.Quote(usage, "test").Amount);
        Assert.Null(usage.Normalize().HitRate);
    }

    [Fact]
    public void InvalidCacheCountsCannotProduceNegativeInputOrCost()
    {
        var usage = Usage(100, 200, 50);
        Assert.True(usage.Normalize().Invalid);
        Assert.Null(usage.Normalize().Input);
        Assert.Null(usage.Normalize().HitRate);
        Assert.Null(Price.Quote(usage, "test").Amount);
    }

    [Fact]
    public void PricingSubtractsCachedInputAndSplitsHourWrites()
    {
        Assert.Equal(0.00156m, Price.Quote(Usage(), "test").Amount);
        var claude = Usage(100, 800, 50) with
        {
            InputAccounting = CacheInputAccounting.ExcludesCached,
            CacheCreationTokens = 100, CacheCreation1hTokens = 50,
        };
        Assert.Equal(0.001185m, Price.Quote(claude, "test").Amount);
        Assert.Null(Price.Quote(claude with { CacheCreation1hTokens = 101 }, "test").Amount);
        var missingHourPrice = Price with { Rates = Price.Rates with { CacheWrite1h = null } };
        Assert.Null(missingHourPrice.Quote(claude, "test").Amount);
    }

    [Fact]
    public void ContextTierUsesTheFullPromptAndStrictBoundary()
    {
        var price = Price with { Tiers = new() { new ModelPriceTier { AboveTokens = 1000, Rates = Price.Rates with { Output = 20 } } } };
        Assert.Equal(10, price.At(1000).Output);
        Assert.Equal(20, price.At(1001).Output);
        Assert.Equal(0.00156m, price.Quote(Usage(), "test").Amount);
        Assert.Equal(0.002562m, price.Quote(Usage(1001), "test").Amount);
        Assert.Null(price.Quote(Usage(100, 800) with { InputAccounting = CacheInputAccounting.ExcludesCached }, "test").Amount);
    }

    [Fact]
    public void ExplicitPublicTierWinsOverLegacy200kAndUnknownModelsStayUnknown()
    {
        const string json = """{"openai":{"models":{"test-model":{"id":"test-model","cost":{"input":2,"output":10,"cache_read":0.2,"tiers":[{"tier":{"type":"context","size":272000},"input":4,"output":15,"cache_read":0.4}],"context_over_200k":{"input":99,"output":99}}}}}}""";
        var catalog = ModelPricing.ParseModelsDev(json, DateTimeOffset.UtcNow);
        var price = Assert.Single(catalog.Entries).Price;
        Assert.Equal(2, price.At(250000).Input);
        Assert.Equal(4, price.At(272001).Input);
        var pricing = new ModelPricing(catalog);
        Assert.NotNull(pricing.Find("p", "openai/test-model").Price);
        Assert.Null(pricing.Find("p", "test-model-invented").Price);
    }

    [Fact]
    public void ProviderOverridePrecedesModelOverrideAndExplicitZeroIsFree()
    {
        var free = Price with { Rates = new TokenPrices { Input = 0, Output = 0, CacheRead = 0, CacheWrite = 0 } };
        var pricing = new ModelPricing(overrides: new[]
        {
            new ModelPriceOverride { Model = "test-model", Price = Price },
            new ModelPriceOverride { ProviderId = "p", Model = "test-model", Price = free },
        });
        Assert.Equal(0, pricing.Quote("p", "test-model", Usage()).Amount);
        Assert.Equal(0.00156m, pricing.Quote("q", "test-model", Usage()).Amount);
    }

    [Fact]
    public void AggregateCacheRateIsTokenWeightedAndFailureUsageIsCounted()
    {
        var report = Report(new[] { Row("a", usage: Usage(1000, 900, 100)), Row("b", usage: Usage(100, 0, 100), outcome: RequestOutcome.Failure) });
        Assert.Equal(2, report.Totals.Requests);
        Assert.Equal(1, report.Totals.Failures);
        Assert.Equal(50, report.Totals.SuccessRate);
        Assert.Equal(100.0 * 900 / 1100, report.Totals.HitRate!.Value, 10);
        Assert.Equal(1300m, report.Totals.Tokens);
    }

    [Fact]
    public void SpeedWeightsDecodeWindowsExcludesReasoningAndRejectsBursts()
    {
        var first = Usage(output: 300) with { IsStreaming = true, FirstContentSeconds = 1, FirstTextSeconds = 2, DurationSeconds = 4, ReasoningTokens = 100 };
        var second = Usage(output: 100) with { IsStreaming = true, FirstContentSeconds = 1, DurationSeconds = 5 };
        var burst = Usage(output: 10000) with { IsStreaming = true, FirstContentSeconds = 1, DurationSeconds = 1.01 };
        var report = Report(new[] { Row("a", usage: first), Row("b", usage: second), Row("c", usage: burst) });
        Assert.Equal(50, report.Totals.Speed);
        Assert.Equal(2, report.Totals.SpeedMeasured);
        Assert.Null((first with { FirstTextSeconds = null }).Decode());
        Assert.Null((second with { IsStreaming = false }).Decode());
    }

    [Fact]
    public void DayBucketsAreCompleteAndAllDimensionsAndHeatmapUseTheSameFilter()
    {
        var report = Report(new[] { Row("a", provider: "p"), Row("b", Today.AddDays(-2), provider: "p"), Row("c", provider: "q") },
            new UsageQuery { FirstDate = Today.AddDays(-1), LastDate = Today, Today = Today, Provider = "p" });
        Assert.Equal(2, report.Series.Count);
        Assert.Equal(0, report.Series[0].Totals.Requests);
        Assert.Equal(1, report.Totals.Requests);
        Assert.Equal(2, report.Heatmap.Count);
        Assert.Equal(1, report.Heatmap[Today].Requests);
        Assert.Equal(DayOfWeek.Monday, UsageReport.HeatmapFirst(Today).DayOfWeek);
        Assert.Equal(2, report.Providers.Count);
    }

    [Fact]
    public void CrossDayDuplicatesAndKeepaliveDoNotIncreaseRequestCounts()
    {
        var previous = Row("same", Today.AddDays(-1));
        var current = Row("same");
        var report = Report(new[] { previous, current, Row(ProxyMetrics.KeepAlivePrefix + "x") },
            new UsageQuery { FirstDate = Today.AddDays(-1), LastDate = Today, Today = Today });
        Assert.Equal(1, report.Totals.Requests);
        Assert.Equal(0, report.Series[0].Totals.Requests);
        Assert.Equal(1, report.Series[1].Totals.Requests);
    }

    [Fact]
    public void DayViewHas24HoursAndDoesNotInventAnHourForLegacyRows()
    {
        var unknown = Row("unknown");
        unknown.Request.UpdatedAtUnixMs = 0;
        var report = Report(new[] { Row("known"), unknown });
        Assert.Equal(24, report.Series.Count);
        Assert.Equal(2, report.Totals.Requests);
        Assert.Equal(1, report.UnplacedHours);
        Assert.Equal(1, report.Series.Sum(b => b.Totals.Requests));
    }

    [Fact]
    public void BigIntegerTokenValuesKeepPrecisionAcrossRequests()
    {
        var report = Report(new[] { Row("a", usage: Usage(ulong.MaxValue, 0, 1)), Row("b", usage: Usage(ulong.MaxValue, 0, 1)) });
        Assert.Equal(2m * ulong.MaxValue + 2, report.Totals.Tokens);
    }

    [Fact]
    public void CsvExportsAllRowsWithUnknownsEmptyAndEscapesFormulaAndComma()
    {
        var row = Row("a");
        row.Request.Attribution = row.Request.Attribution! with { ProviderName = "=HYPERLINK(\"bad\")" };
        row.Request.KeyName = "a,b";
        row.Request.Usage = Usage() with { OutputTokens = null };
        using var writer = new StringWriter();
        UsageCsv.Write(writer, Report(new[] { row }).Records);
        var csv = writer.ToString();
        Assert.Contains("\"'=HYPERLINK(\"\"bad\"\")\"", csv);
        Assert.Contains("\"a,b\"", csv);
        Assert.DoesNotContain("未获取", csv);
        Assert.Equal(2, csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void HistorySurvivesRestartRefreshesChangedWalAndReportsDamage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "retry-usage-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var data = new DataDatabase(directory);
            var history = new UsageHistory();
            var journal = new SqliteDailyStorage(data, "r", "test").Open(Today, false).Journal!;
            journal.Append(Row("a").Request);
            Assert.Single(history.Read(data).Records);
            journal.Append(Row("b").Request);
            Assert.Equal(2, history.Read(data).Records.Count);
            data.Statistics.Execute(Today, c =>
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "UPDATE daily_requests SET record_json = 'invalid' WHERE request_id = 'b';";
                cmd.ExecuteNonQuery();
            });
            var result = new UsageHistory().Read(data);
            Assert.Single(result.Records);
            Assert.Single(result.Warnings);
            Assert.Equal(100UL, result.Records[0].Request.Usage!.OutputTokens);
            Assert.Equal("p", result.Records[0].Request.Attribution!.ProviderId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void RequestAttributionFollowsFinalAttemptAndStopsChangingAfterOutcome()
    {
        var directory = Path.Combine(Path.GetTempPath(), "retry-attribution-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var data = new DataDatabase(directory))
            using (var metrics = ProxyMetrics.FromDailyLogs(data, "r", "test"))
            {
                metrics.RequestStarted("one", "POST", "/v1/responses");
                metrics.Attribute("one", new RequestAttribution { ProviderId = "old", KeyId = "key-old" });
                metrics.Attribute("one", new RequestAttribution { ProviderId = "new", KeyId = "key-new", Model = "outbound-model" });
                metrics.Success("one", null, "key-new", usage: Usage());
                metrics.Attribute("one", new RequestAttribution { ProviderId = "later" });
                metrics.RequestFinished("one");
                Assert.Equal(1UL, metrics.Snapshot().SuccessfulRequests);
            }
            using var reopened = new DataDatabase(directory);
            var persisted = Assert.Single(new UsageHistory().Read(reopened).Records).Request;
            Assert.Equal("new", persisted.Attribution!.ProviderId);
            Assert.Equal("key-new", persisted.KeyId);
            Assert.Equal("outbound-model", persisted.Attribution.Model);
            Assert.Equal(Usage(), persisted.Usage);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
