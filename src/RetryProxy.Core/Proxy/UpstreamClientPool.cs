using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;

namespace RetryProxy.Core.Proxy;

/// <summary>按出口（含代理凭据）和 HTTP/TLS 隔离连接；只淘汰没有在读响应的客户端。</summary>
internal sealed class UpstreamClientPool(Func<Uri?, bool, HttpClient> create) : IDisposable
{
    public const int MaxIdleClients = 16;
    private readonly object _lock = new();
    private readonly Dictionary<(string Proxy, bool Secure), Entry> _entries = new();
    private long _clock;
    private bool _disposed;

    private sealed class Entry(HttpClient client, (string Proxy, bool Secure) key)
    {
        public HttpClient Client { get; } = client;
        public (string Proxy, bool Secure) Key { get; } = key;
        public int Users;
        public long LastUse;
        public bool Retired;
    }

    public Lease Rent(Uri? proxy, bool secure)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var key = (proxy?.AbsoluteUri ?? string.Empty, secure);
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new Entry(create(proxy, secure), key);
                _entries.Add(key, entry);
            }
            entry.Users++;
            entry.LastUse = ++_clock;
            return new Lease(entry.Client, () => Release(entry));
        }
    }

    private void Release(Entry entry)
    {
        var dispose = new List<HttpClient>();
        lock (_lock)
        {
            entry.Users--;
            entry.LastUse = ++_clock;
            if (entry.Retired && entry.Users == 0) dispose.Add(entry.Client);
            if (!_disposed)
            {
                foreach (var idle in _entries.Values.Where(item => item.Users == 0).OrderByDescending(item => item.LastUse).Skip(MaxIdleClients).ToArray())
                {
                    _entries.Remove(idle.Key);
                    idle.Retired = true;
                    dispose.Add(idle.Client);
                }
            }
        }
        foreach (var client in dispose) client.Dispose();
    }

    public void Dispose()
    {
        var dispose = new List<HttpClient>();
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var entry in _entries.Values)
            {
                entry.Retired = true;
                if (entry.Users == 0) dispose.Add(entry.Client);
            }
            _entries.Clear();
        }
        foreach (var client in dispose) client.Dispose();
    }

    internal sealed class Lease(HttpClient client, Action release) : IDisposable
    {
        private Action? _release = release;
        public HttpClient Client { get; } = client;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
