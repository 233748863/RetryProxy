using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RetryProxy.Core.Config;
using RetryProxy.Core.Diagnostics;
using RetryProxy.Core.Metrics;
using RetryProxy.Core.Storage;
using Xunit;

namespace RetryProxy.Tests;

public sealed class RequestLogReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "retry-request-reader-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public async Task SummarySnapshotIncludesBothClientsAndMoreThanOnePageWithoutEvents()
    {
        using var data = new DataDatabase(_root);
        using var store = new DiagnosticStore(data);
        for (var index = 0; index < 110; index++)
        {
            var request = store.Begin(new(Guid.NewGuid().ToString("N"), index % 2 == 0 ? ClientType.Codex : ClientType.Claude,
                store.Today, DateTimeOffset.Now, "POST", "/v1/responses"))!;
            request.Record(new(DiagnosticEventKind.SendStarted, 0) { Target = new("p", "Provider", "k", "Key", "model") });
            if (index == 0) continue;
            request.Record(new(DiagnosticEventKind.Outcome, 1) { Outcome = DiagnosticOutcome.Success, StatusCode = 200 });
            request.Record(new(DiagnosticEventKind.Finished, 2));
        }
        var snapshot = await store.ReadSummariesAsync(store.Today);
        Assert.Equal(110, snapshot.Items.Count);
        Assert.Equal(55, snapshot.Items.Count(item => item.Request.Client == ClientType.Codex));
        Assert.Single(snapshot.Items, item => item.Outcome == DiagnosticOutcome.Pending);
        var again = await store.ReadSummariesAsync(store.Today);
        Assert.Equal(snapshot.Items.OrderBy(item => item.Request.RequestId), again.Items.OrderBy(item => item.Request.RequestId));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadSummariesAsync(store.Today, cancel.Token));
    }

    [Fact]
    public void UsageReadFollowsFullIdentityAcrossMonthsAndPreservesDistinctRoutes()
    {
        using var data = new DataDatabase(_root);
        var date = new DateOnly(2026, 10, 31);
        var next = date.AddDays(1);
        void Append(string route, DateOnly day, string id, long updated, ulong tokens) =>
            new SqliteDailyStorage(data, route, route).Open(day, false).Journal!.Append(new DailyRequest
            { RequestId = id, UpdatedAtUnixMs = updated, Outcome = RequestOutcome.Success, Usage = new RequestUsage { InputTokens = tokens } });
        Append("a", date, "abcdef01-complete-one", 1, 10);
        Append("a", next, "abcdef01-complete-one", 2, 20);
        Append("a", next, "abcdef01-complete-two", 3, 900);
        Append("b", next, "abcdef01-complete-one", 4, 30);
        var result = RequestLogUsageReader.Read(data, date, next, ["abcdef01-complete-one"]);
        Assert.Empty(result.Warnings);
        Assert.Equal(2, result.Records.Count);
        Assert.Equal(20UL, result.Records.Single(row => row.RouteId == "a").Request.Usage!.InputTokens);
        Assert.Equal(30UL, result.Records.Single(row => row.RouteId == "b").Request.Usage!.InputTokens);
        Assert.All(result.Records, row => Assert.Equal("abcdef01-complete-one", row.Request.RequestId));
    }

    [Fact]
    public void MissingAndDamagedUsageAreReportedWithoutInventingZeroOrChangingTheDatabase()
    {
        using var data = new DataDatabase(_root);
        var date = new DateOnly(2026, 10, 11);
        new SqliteDailyStorage(data, "a", "a").Open(date, false).Journal!.Append(new DailyRequest { RequestId = "broken", Outcome = RequestOutcome.Success });
        data.Statistics.Execute(date, connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE daily_requests SET record_json = '{' WHERE request_id = 'broken'";
            command.ExecuteNonQuery();
        });
        var missing = RequestLogUsageReader.Read(data, date, date, ["missing"]);
        Assert.Empty(missing.Records); Assert.Empty(missing.Warnings);
        var broken = RequestLogUsageReader.Read(data, date, date, ["broken"]);
        Assert.Empty(broken.Records); Assert.Single(broken.Warnings);
        using var lease = data.Statistics.OpenRead(date)!;
        using var read = lease.Connection.CreateCommand();
        read.CommandText = "SELECT record_json FROM daily_requests WHERE request_id = 'broken'";
        Assert.Equal("{", read.ExecuteScalar());
    }
}
