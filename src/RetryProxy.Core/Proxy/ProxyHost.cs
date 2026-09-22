using System;
using System.Net;
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

namespace RetryProxy.Core.Proxy;

/// <summary>把 <see cref="RetryProxy"/> 挂到 Kestrel 上（对应 proxy.rs 的 router + axum::serve）。</summary>
public sealed class ProxyHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private ProxyHost(WebApplication app)
    {
        _app = app;
    }

    /// <summary>实际绑定的端口（传 0 时启动后才知道）。</summary>
    public int Port { get; private set; }

    public static async Task<ProxyHost> StartAsync(RetryProxy proxy, int port, CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = "RetryProxy",
            EnvironmentName = Environments.Production,
        });
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromMilliseconds(100));
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = RetryProxy.MaxRequestBodyBytes;
            options.Limits.MinRequestBodyDataRate = null;
            options.Limits.MinResponseDataRate = null;
            // 不在管道里缓冲响应：写入只有在数据真正交给 socket 后才完成，
            // 这样中途切断连接时客户端已经拿到之前的全部字节（对应 hyper 的行为）。
            options.Limits.MaxResponseBufferSize = 0;
            options.Listen(IPAddress.Loopback, port);
        });
        var app = builder.Build();
        app.MapGet("/_retry/health", (RequestDelegate)proxy.HealthAsync);
        app.MapFallback((RequestDelegate)proxy.HandleAsync);
        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var host = new ProxyHost(app);
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        if (addresses is not null)
        {
            foreach (var address in addresses.Addresses)
            {
                if (Uri.TryCreate(address, UriKind.Absolute, out var uri))
                {
                    host.Port = uri.Port;
                    break;
                }
            }
        }

        if (host.Port == 0)
        {
            host.Port = port;
        }

        return host;
    }

    /// <summary>硬停：不等在途连接收尾，超过宽限期直接切断。</summary>
    public async Task StopAsync(TimeSpan grace)
    {
        using var cts = new CancellationTokenSource(grace);
        try
        {
            await _app.StopAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
