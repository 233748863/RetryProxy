using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetryProxy.Core.Config;
using RetryProxy.Core.Logging;
using RetryProxy.Core.Metrics;
using RetryProxy.Core.Proxy;
using Xunit;

namespace RetryProxy.Tests.Support;

/// <summary>进程内假上游：Kestrel 监听 127.0.0.1:0，所有请求交给一个委托处理。</summary>
public sealed class FakeUpstream : IAsyncDisposable
{
    private readonly WebApplication _app;

    private FakeUpstream(WebApplication app, int port)
    {
        _app = app;
        Port = port;
        BaseUrl = $"http://127.0.0.1:{port}";
    }

    public int Port { get; }

    public string BaseUrl { get; }

    public static async Task<FakeUpstream> StartAsync(Func<HttpContext, Task> handler, int? port = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromMilliseconds(100));
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MinResponseDataRate = null;
            options.Limits.MinRequestBodyDataRate = null;
            options.Limits.MaxResponseBufferSize = 0;
            options.Listen(IPAddress.Loopback, port ?? 0);
        });
        var app = builder.Build();
        app.Run(new RequestDelegate(handler));
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        var bound = 0;
        foreach (var address in addresses.Addresses)
        {
            bound = new Uri(address).Port;
        }

        return new FakeUpstream(app, bound);
    }

    public async ValueTask DisposeAsync()
    {
        using var cts = new CancellationTokenSource(200);
        try
        {
            await _app.StopAsync(cts.Token);
        }
        catch (Exception)
        {
        }

        await _app.DisposeAsync();
    }
}

/// <summary>假上游响应的小工具。</summary>
public static class Upstream
{
    public static Task Text(HttpContext context, int status, string body, string? contentType = null)
    {
        return Bytes(context, status, Encoding.UTF8.GetBytes(body), contentType);
    }

    public static async Task Bytes(HttpContext context, int status, byte[] body, string? contentType = null, IDictionary<string, string>? headers = null)
    {
        context.Response.StatusCode = status;
        if (contentType is not null)
        {
            context.Response.ContentType = contentType;
        }

        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                context.Response.Headers[name] = value;
            }
        }

        context.Response.ContentLength = body.Length;
        await context.Response.Body.WriteAsync(body);
        await context.Response.CompleteAsync();
    }

    public static Task Json(HttpContext context, int status, string body) => Text(context, status, body, "application/json");

    public static Task EventStream(HttpContext context, string body, IDictionary<string, string>? headers = null)
    {
        return Bytes(context, 200, Encoding.UTF8.GetBytes(body), "text/event-stream", headers);
    }

    /// <summary>开始一个分块响应；后续用 <see cref="Chunk"/> 写数据并刷新。</summary>
    public static async Task Begin(HttpContext context, int status, string? contentType, IDictionary<string, string>? headers = null, long? contentLength = null)
    {
        context.Response.StatusCode = status;
        if (contentType is not null)
        {
            context.Response.ContentType = contentType;
        }

        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                context.Response.Headers[name] = value;
            }
        }

        if (contentLength is { } length)
        {
            context.Response.ContentLength = length;
        }

        await context.Response.StartAsync();
        await context.Response.Body.FlushAsync();
    }

    public static async Task Chunk(HttpContext context, string text) => await Chunk(context, Encoding.UTF8.GetBytes(text));

    public static async Task Chunk(HttpContext context, byte[] bytes)
    {
        await context.Response.Body.WriteAsync(bytes);
        await context.Response.Body.FlushAsync();
    }

    /// <summary>永远不再发数据，直到客户端断开。</summary>
    public static async Task Pending(HttpContext context)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>模拟上游连接被重置：直接切断。</summary>
    public static void Reset(HttpContext context) => context.Abort();

    public static async Task<string> ReadBody(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }
}

/// <summary>一次可等待的通知（对应 tokio::sync::Notify 的单次用法）。</summary>
public sealed class Notify
{
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);

    public void NotifyOne() => _signal.Release();

    public Task Notified(CancellationToken token = default) => _signal.WaitAsync(token);

    public async Task<bool> Notified(TimeSpan timeout) => await _signal.WaitAsync(timeout);
}

/// <summary>对应 request_lifecycle.rs 的 LifecycleProxy：假上游 + 代理（都在端口 0），日志走界面队列。</summary>
public sealed class LifecycleProxy : IAsyncDisposable
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly ProxyHost _host;
    private readonly FakeUpstream _upstream;
    private readonly string _logDirectory;
    private readonly ProxyLogger _logger;

    private LifecycleProxy(ProxyHost host, FakeUpstream upstream, Core.Proxy.RetryProxy proxy, ProxyLogger logger, string logDirectory, CancellationTokenSource cancel)
    {
        _host = host;
        _upstream = upstream;
        Proxy = proxy;
        _logger = logger;
        _logDirectory = logDirectory;
        _cancel = cancel;
        Address = $"http://127.0.0.1:{host.Port}";
    }

    public string Address { get; }

    public int Port => _host.Port;

    public string UpstreamAddress => _upstream.BaseUrl;

    public Core.Proxy.RetryProxy Proxy { get; }

    public ProxyMetrics Metrics => Proxy.Metrics;

    public CancellationToken Cancel => _cancel.Token;

    public void CancelProxy() => _cancel.Cancel();

    public static async Task<LifecycleProxy> StartAsync(Func<HttpContext, Task> upstreamHandler, ProxyConfig config, Func<Core.Proxy.RetryProxy, Core.Proxy.RetryProxy>? customize = null)
    {
        var upstream = await FakeUpstream.StartAsync(upstreamHandler);
        var logDirectory = Path.Combine(Path.GetTempPath(), "retry-proxy-tests", Guid.NewGuid().ToString("N"));
        var logger = ProxyLogger.Create(logDirectory);
        config.UpstreamBaseUrl = upstream.BaseUrl;
        config.ListenPort = 18080;
        var cancel = new CancellationTokenSource();
        var proxy = new Core.Proxy.RetryProxy(config, logger, new ProxyMetrics(), cancel.Token);
        if (customize is not null)
        {
            proxy = customize(proxy);
        }

        var host = await ProxyHost.StartAsync(proxy, 0);
        config.ListenPort = host.Port;
        return new LifecycleProxy(host, upstream, proxy, logger, logDirectory, cancel);
    }

    public async Task<TcpClient> SocketRequest(string path)
    {
        var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, Port);
        var request = Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: 127.0.0.1:{Port}\r\n\r\n");
        await socket.GetStream().WriteAsync(request);
        await socket.GetStream().FlushAsync();
        return socket;
    }

    public async Task<string> CompletedLogs()
    {
        await TestClock.WaitUntil(() => Metrics.Snapshot().ActiveRequests == 0);
        Assert.Empty(Metrics.Snapshot().Requests);
        Assert.Equal(0UL, Proxy.KeepAlive.Snapshot().ActiveRequests);
        return string.Join("\n", DrainLogs());
    }

    public List<string> DrainLogs()
    {
        var lines = new List<string>();
        while (_logger.UiLines!.TryRead(out var line))
        {
            lines.Add(line);
        }

        return lines;
    }

    public async ValueTask DisposeAsync()
    {
        _cancel.Cancel();
        await _host.StopAsync(TimeSpan.FromMilliseconds(200));
        await _host.DisposeAsync();
        await _upstream.DisposeAsync();
        _logger.Dispose();
        try
        {
            Directory.Delete(_logDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public static class TestClock
{
    public static async Task WaitUntil(Func<bool> condition, double seconds = 3.0, string? message = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new Xunit.Sdk.XunitException(message ?? "请求状态没有按期收尾");
            }

            await Task.Delay(10);
        }
    }
}

public static class TestClient
{
    public static HttpClient Create(double timeoutSeconds = 4.0)
    {
        // 释放未读完的响应时立即断开，而不是像默认那样先排空 2 秒，与 reqwest 的 drop 行为一致。
        var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, MaxResponseDrainSize = 0, ResponseDrainTimeout = TimeSpan.Zero };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
    }

    public static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string url, string? body = null, string? contentType = null, IDictionary<string, string>? headers = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, contentType ?? "application/json");
        }

        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                if (!request.Headers.TryAddWithoutValidation(name, value))
                {
                    request.Content?.Headers.TryAddWithoutValidation(name, value);
                }
            }
        }

        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    /// <summary>读完整个正文；连接被代理切断时返回 null。</summary>
    public static async Task<byte[]?> TryReadAll(HttpResponseMessage response)
    {
        try
        {
            return await response.Content.ReadAsByteArrayAsync();
        }
        catch (Exception error) when (error is HttpRequestException or IOException or HttpIOException or OperationCanceledException)
        {
            return null;
        }
    }

    public static async Task<JsonDocument> Health(HttpClient client, string address)
    {
        var text = await client.GetStringAsync($"{address}/_retry/health");
        return JsonDocument.Parse(text);
    }

    /// <summary>从流里读下一块（对应 reqwest 的 chunk()）；EOF 返回 null，连接错误抛异常。</summary>
    public static async Task<byte[]?> NextChunk(Stream stream)
    {
        var buffer = new byte[64 * 1024];
        var read = await stream.ReadAsync(buffer);
        return read == 0 ? null : buffer[..read];
    }
}

public static class LogFiles
{
    /// <summary>日志器仍持有写句柄，读取时必须允许 ReadWrite 共享。</summary>
    public static string ReadAll(string directory)
    {
        using var stream = new FileStream(Path.Combine(directory, "retry-proxy.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

public static class Configs
{
    public static ProxyConfig Default() => new() { UpstreamBaseUrl = "http://127.0.0.1:1" };

    public static ProxyConfig Retrying(double totalTimeoutSeconds)
    {
        var config = Default();
        config.MaxRetries = 100;
        config.TimeoutSeconds = 5.0;
        config.TotalTimeoutSeconds = totalTimeoutSeconds;
        config.BaseDelaySeconds = 0.05;
        config.MaxDelaySeconds = 0.05;
        return config;
    }

    public static ProxyConfig Generation()
    {
        var config = Retrying(3.0);
        config.MaxRetries = 1;
        config.TimeoutSeconds = 1.0;
        config.GenerationTimeoutSeconds = 0.25;
        config.BaseDelaySeconds = 0.0;
        config.MaxDelaySeconds = 0.0;
        return config;
    }

    public const string GeneratedStream =
        "data: {\"type\":\"response.created\",\"response\":{\"id\":\"new-attempt\",\"output\":[]}}\n\n"
        + "data: {\"type\":\"response.output_text.delta\",\"delta\":\"recovered\"}\n\n"
        + "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[]}}\n\n";
}
