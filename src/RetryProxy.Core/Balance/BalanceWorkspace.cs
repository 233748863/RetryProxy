using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RetryProxy.Core.Config;

namespace RetryProxy.Core.Balance;

/// <summary>
/// 余额缓存与调度。和代理工作区一样在界面线程调用；后台只投递结果，由 Poll 检查来源后发布。
/// 例如查询 A 的旧 Key 时改了密钥，旧任务被取消；即使上游晚返回，也不能覆盖新 Key 的余额。
/// </summary>
public sealed class BalanceWorkspace : IDisposable
{
    public static readonly TimeSpan PageRefreshAge = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan BackgroundRefreshAge = TimeSpan.FromMinutes(30);
    public const int MaxConcurrentQueries = 4;
    private readonly Func<BalanceRequest, CancellationToken, Task<BalanceResult>> _fetch;
    private readonly Func<BalanceRequest, BalanceQueryMode, string?>? _rememberDetected;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _slots = new(MaxConcurrentQueries);
    private readonly Dictionary<BalanceKey, Entry> _entries = new();
    private readonly ConcurrentQueue<Completion> _completed = new();
    private readonly HashSet<Task> _workers = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Action? _notifier;
    private bool _disposed;

    private sealed class Entry(BalanceRequest request)
    {
        public BalanceRequest Request { get; set; } = request;
        public BalanceResult? Result { get; set; }
        public DateTimeOffset? CheckedAt { get; set; }
        public string? PersistenceError { get; set; }
        public CancellationTokenSource? Pending { get; set; }
    }

    private sealed record Completion(Entry Entry, BalanceRequest Request, CancellationTokenSource Pending,
        BalanceResult? Result, DateTimeOffset CheckedAt);

    public BalanceWorkspace(Func<BalanceRequest, CancellationToken, Task<BalanceResult>> fetch,
        Func<BalanceRequest, BalanceQueryMode, string?>? rememberDetected = null, TimeProvider? timeProvider = null)
    {
        _fetch = fetch;
        _rememberDetected = rememberDetected;
        _clock = timeProvider ?? TimeProvider.System;
    }

    public void SetUiNotifier(Action? notifier) => _notifier = notifier;

    public void Synchronize(ProxyConfig config)
    {
        if (_disposed) return;
        var present = new HashSet<BalanceKey>();
        foreach (var provider in config.Providers)
        foreach (var key in provider.Keys)
        {
            var request = new BalanceRequest
            {
                Key = new BalanceKey(provider.Id, key.Id), ClientType = provider.ClientType,
                BaseUrl = provider.BaseUrl, ApiKey = key.ApiKey, Query = provider.BalanceQuery.Clone(),
            };
            present.Add(request.Key);
            if (_entries.TryGetValue(request.Key, out var existing) && existing.Request.SameSourceAs(request))
            {
                existing.Request = request;
                continue;
            }
            existing?.Pending?.Cancel();
            _entries[request.Key] = new Entry(request);
        }
        foreach (var key in _entries.Keys.Where(key => !present.Contains(key)).ToList())
        {
            _entries[key].Pending?.Cancel();
            _entries.Remove(key);
        }
    }

    public BalanceSnapshot Get(string providerId, string keyId) => _entries.TryGetValue(new BalanceKey(providerId, keyId), out var entry)
        ? new BalanceSnapshot(entry.Result, entry.CheckedAt, entry.Pending is not null,
            entry.Request.Query.Mode != BalanceQueryMode.None, entry.PersistenceError)
        : BalanceSnapshot.Empty;

    public bool IsRefreshing(string providerId) => _entries.Values.Any(entry => entry.Request.Key.ProviderId == providerId && entry.Pending is not null);

    public void RefreshClient(ClientType client)
    {
        foreach (var entry in _entries.Values.Where(entry => entry.Request.ClientType == client))
            Queue(entry, PageRefreshAge);
    }

    public void RefreshProvider(string providerId)
    {
        foreach (var entry in _entries.Values.Where(entry => entry.Request.Key.ProviderId == providerId))
            Queue(entry, TimeSpan.Zero);
    }

    /// <summary>后台仅刷新当前和已准备的 Key，切换页面不会扩大后台查询范围。</summary>
    public void RefreshBackground(IEnumerable<BalanceKey> keys)
    {
        foreach (var key in keys.Distinct())
            if (_entries.TryGetValue(key, out var entry)) Queue(entry, BackgroundRefreshAge);
    }

    /// <summary>下一项后台余额到期的时间；正在查询的条目由完成通知重新调度，避免重复查询。</summary>
    public DateTimeOffset? NextBackgroundRefreshAt(IEnumerable<BalanceKey> keys)
    {
        DateTimeOffset? next = null;
        foreach (var key in keys.Distinct())
        {
            if (!_entries.TryGetValue(key, out var entry) || entry.Request.Query.Mode == BalanceQueryMode.None || entry.Pending is not null) continue;
            var due = entry.CheckedAt is { } time ? time + BackgroundRefreshAge : _clock.GetUtcNow();
            if (next is null || due < next) next = due;
        }
        return next;
    }

    private void Queue(Entry entry, TimeSpan maxAge)
    {
        if (_disposed || entry.Request.Query.Mode == BalanceQueryMode.None || entry.Pending is not null) return;
        if (entry.CheckedAt is { } last && maxAge > TimeSpan.Zero && _clock.GetUtcNow() - last < maxAge) return;
        var pending = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        entry.Pending = pending;
        _workers.RemoveWhere(task => task.IsCompleted);
        _workers.Add(FetchAsync(entry, entry.Request, pending));
        _notifier?.Invoke();
    }

    private async Task FetchAsync(Entry entry, BalanceRequest request, CancellationTokenSource pending)
    {
        var entered = false;
        BalanceResult? result = null;
        try
        {
            await _slots.WaitAsync(pending.Token).ConfigureAwait(false);
            entered = true;
            result = await _fetch(request, pending.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { }
        catch (Exception)
        {
            // 外部查询实现也可能抛出带密钥的异常；只传播固定文案。
            result = new BalanceResult(null, string.Empty, false, false, null, "余额查询失败，请检查网络与供应商设置");
        }
        finally
        {
            if (entered) _slots.Release();
            _completed.Enqueue(new Completion(entry, request, pending, result, _clock.GetUtcNow()));
            _notifier?.Invoke();
        }
    }

    /// <summary>返回值表示余额展示有变化；自动识别仅在同一来源仍有效时写回配置。</summary>
    public bool Poll()
    {
        var changed = false;
        while (_completed.TryDequeue(out var completion))
        {
            var entry = completion.Entry;
            var canceled = completion.Pending.IsCancellationRequested;
            completion.Pending.Dispose();
            if (_disposed || !_entries.TryGetValue(completion.Request.Key, out var current) || !ReferenceEquals(current, entry)
                || !ReferenceEquals(entry.Pending, completion.Pending)) continue;
            entry.Pending = null;
            changed = true;
            if (canceled || completion.Result is null || !entry.Request.SameSourceAs(completion.Request)) continue;
            entry.Result = completion.Result;
            entry.CheckedAt = completion.CheckedAt;
            entry.PersistenceError = null;
            if (completion.Request.Query.Mode == BalanceQueryMode.Auto && completion.Result.Error is null
                && completion.Result.DetectedMode is BalanceQueryMode.Usage or BalanceQueryMode.UserBalance or BalanceQueryMode.OpenAiBilling
                && entry.Request.Query.Detected != completion.Result.DetectedMode)
            {
                try { entry.PersistenceError = _rememberDetected?.Invoke(completion.Request, completion.Result.DetectedMode.Value); }
                catch (Exception) { entry.PersistenceError = "已查询余额，但识别方式保存失败"; }
                // 回调错误文本也不信任：只让安全的固定消息进入界面。
                if (entry.PersistenceError is not null) entry.PersistenceError = "已查询余额，但识别方式保存失败";
            }
        }
        _workers.RemoveWhere(task => task.IsCompleted);
        return changed;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notifier = null;
        _shutdown.Cancel();
        _entries.Clear();
        // 不阻塞 UI 等待网络；所有工作结束后统一释放，包括已删除条目的取消源。
        _ = Task.WhenAll(_workers.ToArray()).ContinueWith(_ =>
        {
            while (_completed.TryDequeue(out var completion)) completion.Pending.Dispose();
            _shutdown.Dispose();
            _slots.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _workers.Clear();
    }
}
