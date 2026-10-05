using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace RetryProxy.Helpers.Ui;

/// <summary>
/// 剪贴板写入：剪贴板同步工具（远程控制、剪贴板历史等）在内容变化后立即读取，会短暂独占剪贴板；
/// 若本进程写失败后原地重试，工具正等待本进程渲染剪贴板数据，双方互锁导致一直失败。
/// 因此失败时经调度器让出 UI 线程（消息循环得以处理渲染请求）再重试，最多约 6 秒；
/// 期间有新的写入请求时旧请求作废。completed(true 为成功) 在 UI 线程回调。
/// </summary>
public static class ClipboardSafe
{
    private static int _generation;

    public static void SetText(string text, Action<bool> completed, int timeoutMs = 6000)
    {
        var generation = Interlocked.Increment(ref _generation);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            completed(false);
            return;
        }

        var deadline = Environment.TickCount64 + timeoutMs;

        void Attempt()
        {
            if (Volatile.Read(ref _generation) != generation) return;
            try
            {
                Clipboard.SetText(text);
                completed(true);
                return;
            }
            catch (Exception)
            {
                if (Volatile.Read(ref _generation) != generation) return;
                if (Environment.TickCount64 >= deadline)
                {
                    completed(false);
                    return;
                }

                var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    Attempt();
                };
                timer.Start();
            }
        }

        Attempt();
    }
}
