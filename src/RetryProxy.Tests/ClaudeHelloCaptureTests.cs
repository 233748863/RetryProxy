using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using RetryProxy.Core.Tls;
using Xunit;

namespace RetryProxy.Tests;

public class ClaudeHelloCaptureTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(6)]
    public async Task CleanupCancelsLosingReaderAndWaitsForAllTasks(int partialBytes)
    {
        using var cancellation = new CancellationTokenSource();
        using var winnerListener = new TcpListener(IPAddress.Loopback, 0);
        using var loserListener = new TcpListener(IPAddress.Loopback, 0);
        winnerListener.Start();
        loserListener.Start();
        using var winnerClient = new TcpClient();
        using var loserClient = new TcpClient();

        // 三种未胜出状态：还没连接、只到记录头的一半、记录正文尚差一个字节。
        // 先把数据放入本地连接，再启动读取，避免靠固定延时猜测读取进度。
        var record = new byte[] { 0x16, 0x03, 0x03, 0x00, 0x02, 0x01, 0x00 };
        if (partialBytes > 0)
        {
            await loserClient.ConnectAsync((IPEndPoint)loserListener.LocalEndpoint);
            await loserClient.GetStream().WriteAsync(record.AsMemory(0, partialBytes));
        }
        await winnerClient.ConnectAsync((IPEndPoint)winnerListener.LocalEndpoint);
        await winnerClient.GetStream().WriteAsync(record);
        var winner = ClaudeHelloCapture.ReadHelloAsync(winnerListener, cancellation.Token);
        var readStage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loser = ClaudeHelloCapture.ReadHelloAsync(loserListener, cancellation.Token, bytesRead =>
        {
            if (bytesRead == (partialBytes == 6 ? 5 : 0))
            {
                readStage.TrySetResult();
            }
        });
        var exited = Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);

        try
        {
            Assert.Equal(record, await winner.WaitAsync(TimeSpan.FromSeconds(5)));
            if (partialBytes > 0)
            {
                await readStage.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.False(loser.IsCompleted);
            await ClaudeHelloCapture.StopCaptureAsync([winnerListener, loserListener], cancellation, [winner, loser, exited])
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(cancellation.IsCancellationRequested);
            Assert.True(loser.IsCompleted);
            Assert.True(exited.IsCanceled);
            Assert.False(loser.IsFaulted, "关闭监听前应先取消，不能遗留 Not listening 等故障");
            Assert.False(winnerListener.Server.IsBound);
            Assert.False(loserListener.Server.IsBound);
        }
        finally
        {
            await ClaudeHelloCapture.StopCaptureAsync([winnerListener, loserListener], cancellation, [winner, loser, exited]);
        }
    }

    [Fact]
    public async Task CleanupWaitsForEveryTaskEvenWhenOneHasAlreadyFailed()
    {
        using var cancellation = new CancellationTokenSource();
        var cancelObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = Task.FromException(new InvalidOperationException("收尾测试异常"));
        var waiting = WaitForCancellationAsync();
        var cleanup = ClaudeHelloCapture.StopCaptureAsync([], cancellation, [failed, waiting]);
        try
        {
            await cancelObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(cleanup.IsCompleted, "一个任务失败后仍须等待其余任务，不能只观察第一个结束的任务");
            release.SetResult();
            await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(waiting.IsCanceled);
            Assert.True(failed.IsFaulted);
        }
        finally
        {
            release.TrySetResult();
            await cleanup;
        }

        async Task WaitForCancellationAsync()
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
            }
            finally
            {
                cancelObserved.SetResult();
                await release.Task;
            }
        }
    }

    [Fact]
    public async Task CanceledReaderDoesNotRetryStoppedListener()
    {
        using var cancellation = new CancellationTokenSource();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        cancellation.Cancel();
        listener.Stop();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ClaudeHelloCapture.ReadHelloAsync(listener, cancellation.Token));
    }

    [Fact]
    public async Task CleanupSupportsFailureBeforeAnyTaskStarts()
    {
        using var cancellation = new CancellationTokenSource();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        await ClaudeHelloCapture.StopCaptureAsync([listener], cancellation, []);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(listener.Server.IsBound);
    }
}
