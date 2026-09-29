using System;
using System.Threading;
using RetryProxy.Core.Config;

namespace RetryProxy.Core.Proxy;

/// <summary>
/// 通道当前的快照与切换信号（PRD-供应商管理 §4.2、§9）。每次尝试开头 <see cref="Read"/> 一次；
/// <see cref="Update"/> 换上新快照，换了"供应商 · Key"时触发切换信号，还没向客户端输出的尝试收到后改用新 Key 重发。
/// 例：请求正在第 3 次尝试的退避等待里，用户切到"Any · 群号" → 等待立即结束，下一次尝试打到群号。
/// </summary>
internal sealed class ChannelState
{
    private readonly object _lock = new();
    private ChannelSnapshot _snapshot;
    private long _version;
    private CancellationTokenSource _switch = new();
    private int _undelivered;

    public ChannelState(ChannelSnapshot snapshot)
    {
        _snapshot = snapshot;
    }

    public ChannelSnapshot Current
    {
        get
        {
            lock (_lock)
            {
                return _snapshot;
            }
        }
    }

    /// <summary>当前快照、版本号（每次更新加一）与本快照的切换信号。</summary>
    public (ChannelSnapshot Snapshot, long Version, CancellationToken Switch) Read()
    {
        lock (_lock)
        {
            return (_snapshot, _version, _switch.Token);
        }
    }

    /// <summary>
    /// 换上新快照。换了"供应商 · Key"时触发旧的切换信号，返回此刻尚未输出的真实请求数；只改了参数或地址时返回 null。
    /// </summary>
    public int? Update(ChannelSnapshot next)
    {
        CancellationTokenSource previous;
        int undelivered;
        lock (_lock)
        {
            var switched = !_snapshot.SameKeyAs(next);
            _snapshot = next;
            _version++;
            if (!switched)
            {
                return null;
            }

            previous = _switch;
            _switch = new CancellationTokenSource();
            undelivered = _undelivered;
        }

        // 在锁外异步触发：取消回调在线程池上唤醒等待中的请求，不在调用方（界面线程）上同步执行改投逻辑。
        _ = previous.CancelAsync();
        return undelivered;
    }

    /// <summary>登记一个还没向客户端输出的真实请求；释放返回值即注销。</summary>
    public IDisposable EnterUndelivered()
    {
        Interlocked.Increment(ref _undelivered);
        return new Registration(this);
    }

    private sealed class Registration : IDisposable
    {
        private ChannelState? _state;

        public Registration(ChannelState state)
        {
            _state = state;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _state, null) is { } state)
            {
                Interlocked.Decrement(ref state._undelivered);
            }
        }
    }
}
