using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RetryProxy.Core.Config;

namespace RetryProxy.Core.Diagnostics;

public enum DiagnosticEventKind
{
    Started,
    SendStarted,
    ResponseHeaders,
    SendFinished,
    RetryWaiting,
    RetryWaitFinished,
    CompatibilityResend,
    Switched,
    ResponseReady,
    Outcome,
    Delivery,
    Finished,
    WaitingGeneration,
}

public enum DiagnosticOutcome { Pending, Success, Failure, Unknown }
public enum DiagnosticDelivery { NotStarted, Complete, Interrupted, Unknown }

/// <summary>只包含可记录的目标身份；不持有通道快照、认证头或密钥。</summary>
public sealed record DiagnosticTarget(
    string ProviderId,
    string ProviderName,
    string KeyId,
    string KeyName,
    string? Model)
{
    public string Label => string.IsNullOrEmpty(KeyName) ? ProviderName : $"{ProviderName} · {KeyName}";
}

public sealed record DiagnosticRequestInfo(
    string RequestId,
    ClientType Client,
    DateOnly Date,
    DateTimeOffset StartedAt,
    string Method,
    string Endpoint);

/// <summary>一次旁路观察；结果及计数必须来自原控制流，不能由诊断重新判断。</summary>
public sealed record DiagnosticEntry(DiagnosticEventKind Kind, double ElapsedSeconds)
{
    public DiagnosticTarget? Target { get; init; }
    public ulong? AttemptNumber { get; init; }
    public ulong? SendNumber { get; init; }
    public ulong? RetryCount { get; init; }
    public int? StatusCode { get; init; }
    public string? Reason { get; init; }
    public string? ErrorCode { get; init; }
    public string? UpstreamRequestId { get; init; }
    public string? LastEvent { get; init; }
    public double? DurationSeconds { get; init; }
    public double? PlannedWaitSeconds { get; init; }
    public double? FirstContentSeconds { get; init; }
    public DiagnosticOutcome? Outcome { get; init; }
    public DiagnosticDelivery? Delivery { get; init; }
}

public sealed record DiagnosticEvent(
    string SessionId,
    string RequestId,
    DateOnly Date,
    long Sequence,
    DiagnosticRequestInfo? Request,
    DiagnosticEntry Entry)
{
    public int Version { get; init; } = 1;
}

public sealed record DiagnosticSummary(DiagnosticRequestInfo Request)
{
    public DiagnosticTarget? LastTarget { get; init; }
    public DiagnosticOutcome Outcome { get; init; } = DiagnosticOutcome.Pending;
    public DiagnosticDelivery Delivery { get; init; } = DiagnosticDelivery.NotStarted;
    public ulong SendCount { get; init; }
    public ulong RetryCount { get; init; }
    public double? FirstContentSeconds { get; init; }
    public double? TotalSeconds { get; init; }
    public double LastElapsedSeconds { get; init; }
    public int? StatusCode { get; init; }
    public string? FailureReason { get; init; }
    public bool Finished { get; init; }
    public bool Incomplete { get; init; }
    public bool PreviousSession { get; init; }
}

public sealed record DiagnosticQuery(DateOnly Date, ClientType Client)
{
    public DiagnosticOutcome? Outcome { get; init; }
    public string? ProviderId { get; init; }
    public string? KeyId { get; init; }
    public string? Model { get; init; }
    public string? RequestId { get; init; }
    public int Offset { get; init; }
    public int PageSize { get; init; } = 100;
}

public sealed record DiagnosticChoice(string Id, string Name, string? ProviderId = null);

public sealed record DiagnosticFilters(
    IReadOnlyList<DiagnosticChoice> Providers,
    IReadOnlyList<DiagnosticChoice> Keys,
    IReadOnlyList<string> Models)
{
    public static DiagnosticFilters Empty { get; } = new([], [], []);
}

public sealed record DiagnosticPage(
    IReadOnlyList<DiagnosticSummary> Items,
    bool HasMore,
    DiagnosticFilters Filters,
    string? Warning);

public sealed record DiagnosticDetail(
    DiagnosticSummary? Summary,
    IReadOnlyList<DiagnosticEvent> Events,
    bool HasMore,
    string? Warning);

public interface IDiagnosticRequest
{
    void Record(DiagnosticEntry entry);
}

public interface IRequestDiagnostics
{
    IDiagnosticRequest? Begin(DiagnosticRequestInfo request);
}

public interface IDiagnosticRepository : IRequestDiagnostics, IDisposable
{
    event Action? Changed;
    DateOnly Today { get; }
    string? Warning { get; }
    Task<DiagnosticPage> QueryAsync(DiagnosticQuery query, CancellationToken cancellationToken = default);
    Task<DiagnosticDetail> ReadDetailAsync(DateOnly date, string requestId, int offset = 0,
        int pageSize = 100, CancellationToken cancellationToken = default);
    Task FlushAsync(CancellationToken cancellationToken = default);
}
