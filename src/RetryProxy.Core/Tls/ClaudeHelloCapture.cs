using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RetryProxy.Core.Cli;
using RetryProxy.Core.Internal;
using RetryProxy.Core.KeepAlive;

namespace RetryProxy.Core.Tls;

/// <summary>
/// 抓取本机 Claude Code 的 ClientHello（TLS 握手的第一个包，里面是客户端支持的套件、扩展等，决定 JA3/JA4 指纹）。
/// 做法：在回环地址开一个端口，用 <c>--settings</c> 临时把 ANTHROPIC_BASE_URL 指向 https://localhost:端口，
/// 后台运行一次 <c>claude --print</c>，读到第一个 TLS 握手记录后立即结束进程。
/// 握手不会完成，也不会发出任何 API 请求。
/// 例：端口 51234 → 子进程收到 <c>{"env":{"ANTHROPIC_BASE_URL":"https://localhost:51234",…}}</c>，
/// 连上后发来约 1.5 KB 的 <c>16 03 01 05 d3 01 …</c>，读完这一条记录就结束子进程。
/// 启动、清理与报错文案沿用 <see cref="CliSession"/>（临时工作目录、三路管道、无窗口、Job Object）。
/// </summary>
public static class ClaudeHelloCapture
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>TLS 明文记录最长 16 KiB，加 5 字节记录头。</summary>
    private const int MaxRecordBytes = 5 + 16 * 1024;

    /// <summary>
    /// 返回抓到的完整记录（含 5 字节记录头）。失败只抛 <see cref="CliException"/> 或 <see cref="TimeoutException"/>，
    /// 消息是固定文案，可直接写日志（不含原始错误文本）。
    /// </summary>
    public static async Task<byte[]> CaptureAsync(CliCommand? commandOverride, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var configured = commandOverride?.Clone() ?? CliCommand.Discover(KeepAliveFlavor.Claude);
        using var timer = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);
        var listeners = StartListeners();
        var pending = new List<Task>();
        Process? child = null;
        ProcessJob? job = null;
        string? directory = null;
        try
        {
            var port = ((IPEndPoint)listeners[0].LocalEndpoint).Port;
            directory = CreateDirectory();
            child = Start(configured, directory, $"https://localhost:{port}");
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    job = ProcessJob.Attach(child);
                }
                catch (CliException)
                {
                    CliSession.TryKill(child);
                    throw;
                }
            }

            var exited = child.WaitForExitAsync(linked.Token);
            pending.Add(exited);
            var accepts = new List<Task<byte[]>>();
            foreach (var listener in listeners)
            {
                var accept = ReadHelloAsync(listener, linked.Token);
                accepts.Add(accept);
                pending.Add(accept);
            }

            var hello = await Task.WhenAny(Task.WhenAny(accepts), exited).ConfigureAwait(false);
            if (hello != exited)
            {
                return await await Task.WhenAny(accepts).ConfigureAwait(false);
            }

            await exited.ConfigureAwait(false);
            // 进程已退出，但连接可能刚被接受还没读完，再给读取 500 毫秒。
            var late = Task.WhenAny(accepts);
            if (await Task.WhenAny(late, Task.Delay(500, linked.Token)).ConfigureAwait(false) == late)
            {
                return await await late.ConfigureAwait(false);
            }

            throw new CliException($"{KeepAliveFlavor.Claude.Label()} 未发起 TLS 连接就退出了（退出码 {child.ExitCode}）");
        }
        catch (OperationCanceledException) when (timer.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{timeout.TotalSeconds:F0} 秒内未收到 {KeepAliveFlavor.Claude.Label()} 的 TLS 握手");
        }
        finally
        {
            await StopCaptureAsync(listeners, linked, pending).ConfigureAwait(false);

            // 与 CliSession.Dispose 一致：先终止整组并等 5 秒，再兜底结束进程树。
            if (child is not null)
            {
                job?.Terminate(child);
                CliSession.TryKill(child);
                job?.Dispose();
                child.Dispose();
            }

            if (directory is not null)
            {
                CliSession.TryDeleteDirectory(directory);
            }
        }
    }

    /// <summary>
    /// 先取消抓取，再关闭监听并等待全部任务结束，包括未胜出的读取和进程退出等待。
    /// 例：IPv4 已读到首包，IPv6 仍在等连接；只关端口会让后者重试已停止的监听器。
    /// </summary>
    internal static async Task StopCaptureAsync(IReadOnlyList<TcpListener> listeners, CancellationTokenSource cancellation, IReadOnlyList<Task> pending)
    {
        cancellation.Cancel();
        foreach (var listener in listeners)
        {
            listener.Stop();
        }

        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 主流程已决定首包或失败原因；这里只观察所有收尾异常，避免覆盖原结果或遗留未观察异常。
        }
    }

    /// <summary>localhost 可能先解析到 ::1，所以 127.0.0.1 与 ::1 都监听同一个端口（端口由系统分配）。</summary>
    private static List<TcpListener> StartListeners()
    {
        var listeners = new List<TcpListener>();
        try
        {
            var v4 = new TcpListener(IPAddress.Loopback, 0);
            v4.Start();
            listeners.Add(v4);
        }
        catch (SocketException error)
        {
            throw new CliException($"无法开启本地抓取端口（{error.SocketErrorCode}）");
        }

        try
        {
            var v6 = new TcpListener(IPAddress.IPv6Loopback, ((IPEndPoint)listeners[0].LocalEndpoint).Port);
            v6.Start();
            listeners.Add(v6);
        }
        catch (SocketException)
        {
            // 本机没有 IPv6 或端口被占时只用 IPv4；Claude Code 解析不到 ::1 时会连 127.0.0.1。
        }

        return listeners;
    }

    private static string CreateDirectory()
    {
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), $"retry-proxy-tls-capture-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            return directory;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new CliException("无法创建指纹抓取工作目录");
        }
    }

    private static Process Start(CliCommand configured, string directory, string baseUrl)
    {
        var start = new ProcessStartInfo(configured.Program)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in configured.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in configured.Environment)
        {
            start.Environment[name] = value;
        }

        // 进程环境与 --settings 双重覆盖（与 CliSession 一致），保证只连本机端口：
        // NO_PROXY 让它绕过系统代理；占位密钥让未登录的 Claude Code 也会发请求（握手到第一个包就被截断，密钥不会发出）。
        var environment = new Dictionary<string, string>
        {
            ["ANTHROPIC_BASE_URL"] = baseUrl,
            ["ANTHROPIC_AUTH_TOKEN"] = "retry-proxy-tls-capture",
            ["NO_PROXY"] = "localhost,127.0.0.1,::1",
            ["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1",
        };
        var settingsEnvironment = new JsonObject();
        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
            settingsEnvironment[name] = value;
        }

        start.Environment.Remove("ANTHROPIC_API_KEY");
        start.Environment.Remove("CLAUDECODE");
        var settings = JsonText.Serialize(new JsonObject
        {
            ["disableAllHooks"] = true,
            ["env"] = settingsEnvironment,
        });
        foreach (var argument in new[]
                 {
                     "--print", "--no-session-persistence", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}",
                     "--disable-slash-commands", "--settings", settings, "--tools", string.Empty, "hi",
                 })
        {
            start.ArgumentList.Add(argument);
        }

        Process child;
        try
        {
            child = Process.Start(start) ?? throw new CliException($"无法启动 {KeepAliveFlavor.Claude.Label()} CLI：Other");
        }
        catch (Win32Exception error)
        {
            throw new CliException($"无法启动 {KeepAliveFlavor.Claude.Label()} CLI：{CliSession.IoKind(error)}");
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            throw new CliException($"无法启动 {KeepAliveFlavor.Claude.Label()} CLI：Other");
        }

        child.StandardInput.Close();
        _ = DrainAsync(child.StandardOutput.BaseStream);
        _ = DrainAsync(child.StandardError.BaseStream);
        return child;
    }

    /// <summary>读空子进程输出，避免管道写满把它卡住；进程结束后读取自然结束。</summary>
    private static async Task DrainAsync(Stream stream)
    {
        try
        {
            await stream.CopyToAsync(Stream.Null).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// 接受连接并读取第一个 TLS 记录；不是握手记录（首字节不是 0x16）或中途断开的连接直接丢弃，继续等下一个。
    /// 例：先来一个 <c>GET / HTTP/1.1</c> 明文连接 → 丢弃；再来 <c>16 03 01 05 d3 …</c> → 读满 5+0x05d3 字节返回。
    /// </summary>
    internal static async Task<byte[]> ReadHelloAsync(TcpListener listener, CancellationToken cancellationToken, Action<int>? bytesReadForTest = null)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException) when (!cancellationToken.IsCancellationRequested)
            {
                continue;
            }

            using (client)
            {
                var stream = client.GetStream();
                var header = new byte[5];
                try
                {
                    // 测试按已读取字节数确认阶段：0 表示已接入连接，5 表示记录头读完。
                    bytesReadForTest?.Invoke(0);
                    await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
                    var length = (header[3] << 8) | header[4];
                    if (header[0] != 0x16 || 5 + length > MaxRecordBytes)
                    {
                        continue;
                    }

                    var record = new byte[5 + length];
                    header.CopyTo(record, 0);
                    bytesReadForTest?.Invoke(header.Length);
                    await stream.ReadExactlyAsync(record.AsMemory(5), cancellationToken).ConfigureAwait(false);
                    return record;
                }
                catch (IOException)
                {
                    // EndOfStreamException 也是 IOException：对方没发完就断开。
                }
            }
        }
    }
}
