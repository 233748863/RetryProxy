using System;
using System.IO;
using System.Threading;
using RetryProxy.Core.Logging;
using Xunit;

namespace RetryProxy.Tests;

public class ProxyLoggerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "retry-proxy-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void RotatesLogFilesWhenSizeLimitIsReached()
    {
        using var logger = ProxyLogger.Silent(_directory);
        var chunk = new string('x', 32 * 1024);
        for (var i = 0; i < 170; i++)
        {
            logger.Info(chunk);
        }

        Assert.True(File.Exists(Path.Combine(_directory, "retry-proxy.log.1")));
        var current = new FileInfo(Path.Combine(_directory, "retry-proxy.log")).Length;
        Assert.True(current <= ProxyLogger.MaxLogBytes);
    }

    [Fact]
    public void QueuedGuiLinesNotifyUiButSilentLoggerDoesNot()
    {
        using var logger = ProxyLogger.Create(_directory);
        var count = 0;
        logger.SetUiNotifier(() => Interlocked.Increment(ref count));
        logger.Info("first");
        logger.Route("alpha").Warn("second");
        Assert.Equal(2, count);

        var reader = logger.UiLines!;
        var lines = 0;
        while (reader.TryRead(out var line))
        {
            lines++;
            Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} (INFO \[系统\] first|WARNING \[通道代理\]\[alpha\] second)$", line);
        }

        Assert.Equal(2, lines);

        using var silent = ProxyLogger.Silent(_directory);
        silent.SetUiNotifier(() => Interlocked.Increment(ref count));
        silent.Error("dropped");
        Assert.Equal(2, count);
        Assert.Null(silent.UiLines);
    }

    [Fact]
    public void RoutePrefixHugsBracketedMessages()
    {
        using var logger = ProxyLogger.Create(_directory);
        var route = logger.Route(" beta ");
        route.Info("[req-1] 开始");
        route.Info("普通消息");
        logger.Route(string.Empty).Info("无前缀");

        var reader = logger.UiLines!;
        Assert.True(reader.TryRead(out var first));
        Assert.EndsWith(" INFO [通道代理][beta][req-1] 开始", first);
        Assert.True(reader.TryRead(out var second));
        Assert.EndsWith(" INFO [通道代理][beta] 普通消息", second);
        Assert.True(reader.TryRead(out var third));
        Assert.EndsWith(" INFO [通道代理] 无前缀", third);
    }

    [Fact]
    public void ActivityLoggersKeepIndependentPreparationSeparateFromChannelKeepAlive()
    {
        using var logger = ProxyLogger.Create(_directory);
        var route = logger.Route("alpha");
        route.WithActivity(LogActivity.Preparation).Info("[会话 12345678] 第 1 轮");
        route.WithActivity(LogActivity.KeepAlive).Info("[会话 12345678] 第 2 轮");
        var preparation = logger.Preparation("准备 1 · Codex");
        var preparing = preparation.ForRequest("保活-ffffffff", internalRequest: true, preparing: true);
        var keepingAlive = preparation.ForRequest("保活-eeeeeeee", internalRequest: true, preparing: false);
        preparing.Warn("[保活-ffffffff] 上游 HTTP 500");
        keepingAlive.Info("[保活-eeeeeeee] 上游 HTTP 200");
        route.Info("供应商保活只是正文里的描述");

        var reader = logger.UiLines!;
        Assert.True(reader.TryRead(out var line));
        Assert.Contains("[通道保活][alpha][准备][会话 12345678]", line);
        Assert.True(reader.TryRead(out line));
        Assert.Contains("[通道保活][alpha][自动保活][会话 12345678]", line);
        Assert.True(reader.TryRead(out line));
        Assert.Contains("[一键准备][准备 1 · Codex][准备][请求 ffffffff]", line);
        Assert.True(reader.TryRead(out line));
        Assert.Contains("[一键准备][准备 1 · Codex][独立保活][请求 eeeeeeee]", line);
        Assert.True(reader.TryRead(out line));
        Assert.Contains("[通道代理][alpha] 供应商保活只是正文里的描述", line);
    }
}
