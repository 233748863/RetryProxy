using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetryProxy.Core.Cli;
using RetryProxy.Core.Config;
using RetryProxy.Core.Logging;
using RetryProxy.Core.Metrics;
using RetryProxy.Core.Proxy;
using RetryProxy.Core.Service;
using RetryProxy.Core.Tls;
using RetryProxy.Tests.Support;
using Xunit;

namespace RetryProxy.Tests;

/// <summary>Claude Code TLS 指纹：解析与复现、指纹更新、抓取，以及经指纹 TLS 转发的端到端行为。</summary>
[Collection("cli-environment")]
public class TlsFingerprintTests
{
    private const string ClaudeCodeJa4 = "t13d1713h1_5b57614c22b0_6a3d802a7139";

    // ------------------------------------------------------------------ 指纹解析与复现

    [Fact]
    public void BuiltinFingerprintIsClaudeCodeAndReproducible()
    {
        var fingerprint = TlsFingerprintStore.Builtin;
        Assert.Equal(ClaudeCodeJa4, fingerprint.Ja4);
        Assert.Equal("localhost", fingerprint.ServerName);
        Assert.Equal(new[] { "http/1.1" }, fingerprint.Protocols);
        Assert.Equal(new[] { 0x11EC, 0x001D }, fingerprint.KeyShareGroups);
        Assert.Equal(new[] { 0x0304, 0x0303 }, fingerprint.Versions);
        Assert.Null(fingerprint.UnsupportedReason());

        // 同时守护 OrderedClientProtocol 依赖的 BouncyCastle internal 成员：改名后这里会抛 MissingMemberException。
        Assert.Null(TlsFingerprintStore.Check(fingerprint));
    }

    [Fact]
    public void ReproducedHelloKeepsExtensionOrderWithFreshRandomAndKeys()
    {
        var fingerprint = TlsFingerprintStore.Builtin;
        var first = TlsFingerprintStore.Reproduce(fingerprint);
        var second = TlsFingerprintStore.Reproduce(fingerprint);
        Assert.Equal(fingerprint.ExtensionOrder, first.ExtensionOrder);
        Assert.True(first.SameStructure(fingerprint));
        Assert.Equal(fingerprint.Record.Length, first.Record.Length);
        Assert.False(first.Record.AsSpan(11, 32).SequenceEqual(second.Record.AsSpan(11, 32)));
        Assert.False(first.Record.AsSpan().SequenceEqual(first.Masked()));
    }

    [Fact]
    public void RejectsFingerprintsThatCannotBeReproduced()
    {
        var (head, extensions) = Split(TlsFingerprintStore.Builtin.Record);

        var grease = (byte[])head.Clone();
        grease[2 + 32 + 1 + 32 + 2] = 0x0a;
        grease[2 + 32 + 1 + 32 + 3] = 0x0a;
        Assert.Contains("GREASE", ClientHelloFingerprint.Parse(Join(grease, extensions)).UnsupportedReason());

        var compressCertificate = extensions.Select(item => item.Type == 18 ? (27, new byte[] { 2, 0, 2 }) : item).ToList();
        Assert.Contains("扩展 27", ClientHelloFingerprint.Parse(Join(head, compressCertificate)).UnsupportedReason());

        var padded = extensions.Append((21, new byte[16])).ToList();
        Assert.Contains("扩展 21", ClientHelloFingerprint.Parse(Join(head, padded)).UnsupportedReason());

        // 参数都受支持但内容与本实现生成的不同（status_request 请求了 responder），复现比对不通过。
        var status = extensions.Select(item => item.Type == 5 ? (5, new byte[] { 1, 0, 2, 0, 0, 0, 0 }) : item).ToList();
        var modified = ClientHelloFingerprint.Parse(Join(head, status));
        Assert.Null(modified.UnsupportedReason());
        Assert.NotNull(TlsFingerprintStore.Check(modified));

        Assert.Throws<FormatException>(() => ClientHelloFingerprint.Parse(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n\r\n")));
        Assert.Throws<FormatException>(() => ClientHelloFingerprint.Parse(TlsFingerprintStore.Builtin.Record.AsSpan(0, 200)));
    }

    // ------------------------------------------------------------------ 指纹更新

    [Fact]
    public void StoreKeepsSameFingerprintAndAdoptsChangedOne()
    {
        var directory = TempDirectory();
        try
        {
            var store = new TlsFingerprintStore(_ => throw new InvalidOperationException());
            store.Configure(directory);
            Assert.Equal("内置", store.Source);

            var same = TlsFingerprintStore.Reproduce(TlsFingerprintStore.Builtin);
            Assert.Equal(FingerprintUpdateOutcome.Unchanged, store.Apply(same.Record).Outcome);
            Assert.False(File.Exists(Path.Combine(directory, TlsFingerprintStore.FileName)));

            var reordered = Reordered();
            var update = store.Apply(reordered.Record);
            Assert.Equal(FingerprintUpdateOutcome.Updated, update.Outcome);
            Assert.Null(update.Reason);
            Assert.Same(TlsFingerprintStore.Builtin, update.Previous);
            Assert.Equal(reordered.ExtensionOrder, store.Current.ExtensionOrder);
            Assert.True(store.Current.Record.AsSpan().SequenceEqual(reordered.Masked()));
            Assert.StartsWith("本机抓取于", store.Source);

            var rejected = store.Apply(Encoding.ASCII.GetBytes("not a hello"));
            Assert.Equal(FingerprintUpdateOutcome.Rejected, rejected.Outcome);
            Assert.True(store.Current.SameStructure(reordered));

            // 重启后沿用保存的指纹；文件损坏时退回内置。
            var restarted = new TlsFingerprintStore(_ => throw new InvalidOperationException());
            restarted.Configure(directory);
            Assert.True(restarted.Current.SameStructure(reordered));
            Assert.StartsWith("本机抓取于", restarted.Source);

            File.WriteAllBytes(Path.Combine(directory, TlsFingerprintStore.FileName), new byte[] { 1, 2, 3 });
            var corrupted = new TlsFingerprintStore(_ => throw new InvalidOperationException());
            corrupted.Configure(directory);
            Assert.Same(TlsFingerprintStore.Builtin, corrupted.Current);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RefreshCapturesOncePerIntervalAndLogsResult()
    {
        var directory = TempDirectory();
        try
        {
            using var logger = ProxyLogger.Silent(directory);
            var route = logger.Route("指纹");
            var captures = 0;
            var store = new TlsFingerprintStore(_ =>
            {
                captures++;
                return Task.FromResult(Reordered().Record);
            });
            await store.RefreshAsync(route, CancellationToken.None);
            await store.RefreshAsync(route, CancellationToken.None);
            Assert.Equal(1, captures);
            Assert.Equal(Reordered().ExtensionOrder, store.Current.ExtensionOrder);

            // 到期后再检查一次：结构没变，只记“一致”。
            store.MakeRefreshDueForTest();
            await store.RefreshAsync(route, CancellationToken.None);
            Assert.Equal(2, captures);

            var failures = 0;
            var failing = new TlsFingerprintStore(_ =>
            {
                failures++;
                throw new CliException("未找到本机 Claude Code CLI，请先安装并完成配置");
            });
            await failing.RefreshAsync(route, CancellationToken.None);
            await failing.RefreshAsync(route, CancellationToken.None);
            Assert.Equal(1, failures);
            Assert.Same(TlsFingerprintStore.Builtin, failing.Current);

            // 意料之外的异常只记类型，原始错误文本（可能含路径、密钥）不落日志。
            var unexpected = new TlsFingerprintStore(_ => throw new InvalidOperationException("sk-secret C:\\Users\\someone"));
            await unexpected.RefreshAsync(route, CancellationToken.None);

            logger.Dispose();
            var logs = LogFiles.ReadAll(directory);
            Assert.Contains($"本机 Claude Code TLS 指纹已变化，新连接改用新指纹：JA4 {ClaudeCodeJa4} → {ClaudeCodeJa4}", logs);
            Assert.Contains($"已核对本机 Claude Code TLS 指纹，与当前一致（JA4 {ClaudeCodeJa4}）", logs);
            Assert.Contains($"抓取本机 Claude Code TLS 指纹失败：未找到本机 Claude Code CLI，请先安装并完成配置；继续使用内置指纹（JA4 {ClaudeCodeJa4}）", logs);
            Assert.Contains("抓取本机 Claude Code TLS 指纹失败：抓取时出现异常（InvalidOperationException）", logs);
            Assert.DoesNotContain("sk-secret", logs);
            Assert.DoesNotContain("someone", logs);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    // ------------------------------------------------------------------ 抓取

    [Fact]
    public async Task CaptureReadsFirstTlsRecordFromCli()
    {
        var command = FixtureCli();
        command.Environment.Add(new("FAKE_CLIENT_HELLO", Convert.ToBase64String(TlsFingerprintStore.Builtin.Record)));
        var started = System.Diagnostics.Stopwatch.StartNew();
        var record = await ClaudeHelloCapture.CaptureAsync(command, TimeSpan.FromSeconds(20), CancellationToken.None);
        Assert.Equal(TlsFingerprintStore.Builtin.Record, record);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(15), "抓到后应立即结束 CLI，而不是等它自己退出");
    }

    [Fact]
    public async Task CaptureFailsWhenCliNeverConnects()
    {
        var exits = new CliCommand("powershell.exe");
        exits.Arguments.AddRange(new[] { "-NoProfile", "-Command", "exit 7; #" });
        var exited = await Assert.ThrowsAsync<CliException>(() => ClaudeHelloCapture.CaptureAsync(exits, TimeSpan.FromSeconds(20), CancellationToken.None));
        Assert.Contains("未发起 TLS 连接就退出了（退出码 7）", exited.Message);

        var sleeps = new CliCommand("powershell.exe");
        sleeps.Arguments.AddRange(new[] { "-NoProfile", "-Command", "Start-Sleep -Seconds 30; #" });
        await Assert.ThrowsAsync<TimeoutException>(() => ClaudeHelloCapture.CaptureAsync(sleeps, TimeSpan.FromSeconds(2), CancellationToken.None));

        // 启动失败与 CliSession 同一套文案：只给错误类别，不带系统原始错误文本。
        var missing = new CliCommand(Path.Combine(Path.GetTempPath(), "retry-proxy-tests", Guid.NewGuid().ToString("N"), "claude.exe"));
        var notFound = await Assert.ThrowsAsync<CliException>(() => ClaudeHelloCapture.CaptureAsync(missing, TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal("无法启动 Claude Code CLI：NotFound", notFound.Message);
    }

    // ------------------------------------------------------------------ 端到端

    [Fact]
    public void OnlyClaudeHttpsChannelsUseFingerprint()
    {
        using var logger = ProxyLogger.Silent(TempDirectory());
        bool Uses(ClientType client, string upstream) => new Core.Proxy.RetryProxy(
            new ProxyConfig { ClientType = client, UpstreamBaseUrl = upstream },
            logger, new ProxyMetrics(), CancellationToken.None).UsesTlsFingerprint;

        // 不再有开关：Claude 通道连 https 上游默认使用指纹。
        Assert.True(Uses(ClientType.Claude, "https://api.example.com"));
        Assert.False(Uses(ClientType.Claude, "http://127.0.0.1:1"));
        Assert.False(Uses(ClientType.Codex, "https://api.example.com"));
    }

    [Fact]
    public void RewritesHttpsTargetToPlainRequestWithOriginalHost()
    {
        var target = new Uri("https://api.example.com/v1/messages?beta=true");
        Assert.Equal("http://api.example.com:443/v1/messages?beta=true", TlsFingerprintConnector.PlainRequestUri(target).AbsoluteUri);
        Assert.Equal("api.example.com", TlsFingerprintConnector.HostHeader(target));
        Assert.Equal("relay.example.com:8443", TlsFingerprintConnector.HostHeader(new Uri("https://relay.example.com:8443/v1")));
        Assert.Equal("[::1]:8443", TlsFingerprintConnector.HostHeader(new Uri("https://[::1]:8443/v1")));
    }

    [Fact]
    public async Task SendsClaudeCodeClientHelloToUpstream()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var captured = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var header = new byte[5];
            await client.GetStream().ReadExactlyAsync(header);
            var record = new byte[5 + ((header[3] << 8) | header[4])];
            header.CopyTo(record, 0);
            await client.GetStream().ReadExactlyAsync(record.AsMemory(5));
            return record;
        });

        await using var proxy = await TlsProxy.StartAsync($"https://localhost:{port}", null);
        using var http = TestClient.Create(10);
        _ = TestClient.Send(http, HttpMethod.Post, $"{proxy.Address}/v1/messages", "{}", "application/json");
        var hello = ClientHelloFingerprint.Parse(await captured.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(ClaudeCodeJa4, hello.Ja4);
        Assert.True(hello.SameStructure(TlsFingerprintStore.Builtin));
    }

    [Fact]
    public async Task ForwardsStreamingResponsesAndReusesConnection()
    {
        var hosts = new ConcurrentQueue<string>();
        var bodies = new ConcurrentQueue<string>();
        var connections = new ConcurrentQueue<string>();
        var protocols = new ConcurrentQueue<SslProtocols>();
        var events = new List<string>
        {
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"model\":\"claude-test\",\"usage\":{\"input_tokens\":1200,\"output_tokens\":1}}}\n\n",
        };
        events.AddRange(Enumerable.Range(0, 20).Select(i =>
            $"event: content_block_delta\ndata: {{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"text_delta\",\"text\":\"{i}{new string('x', 3000)}\"}}}}\n\n"));
        events.Add("event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":42}}\n\n");
        events.Add("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");
        await using var upstream = await HttpsUpstream.StartAsync(async context =>
        {
            hosts.Enqueue(context.Request.Host.Value!);
            connections.Enqueue(context.Connection.Id);
            protocols.Enqueue(context.Features.Get<ITlsHandshakeFeature>()!.Protocol);
            bodies.Enqueue(await Upstream.ReadBody(context));
            await Upstream.Begin(context, 200, "text/event-stream");
            foreach (var item in events)
            {
                await Upstream.Chunk(context, item);
                await Task.Delay(5);
            }
        });
        await using var proxy = await TlsProxy.StartAsync(upstream.BaseUrl, upstream.Certificate);
        Assert.True(proxy.Proxy.UsesTlsFingerprint);

        using var http = TestClient.Create(20);
        var requests = new List<string>();
        for (var round = 0; round < 3; round++)
        {
            var request = $"{{\"model\":\"claude-test\",\"round\":{round},\"stream\":true}}";
            requests.Add(request);
            using var response = await TestClient.Send(http, HttpMethod.Post, $"{proxy.Address}/v1/messages?beta=true", request, "application/json");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(string.Concat(events), await response.Content.ReadAsStringAsync());
        }

        Assert.Equal(requests, bodies.ToArray());
        Assert.All(hosts, host => Assert.Equal($"localhost:{upstream.Port}", host));
        Assert.Single(connections.Distinct());
        Assert.All(protocols, protocol => Assert.True(protocol is SslProtocols.Tls12 or SslProtocols.Tls13));
        Assert.Contains("输入 1200 / 输出 42 token", await proxy.Logs());
    }

    [Fact]
    public async Task RejectsUntrustedUpstreamCertificate()
    {
        var reached = 0;
        await using var upstream = await HttpsUpstream.StartAsync(context =>
        {
            Interlocked.Increment(ref reached);
            return Upstream.Json(context, 200, "{}");
        });
        await using var proxy = await TlsProxy.StartAsync(upstream.BaseUrl, null);
        using var http = TestClient.Create(10);
        using var response = await TestClient.Send(http, HttpMethod.Post, $"{proxy.Address}/v1/messages", "{}", "application/json");
        Assert.False(response.IsSuccessStatusCode);
        Assert.Equal(0, reached);
        Assert.Contains("连不上上游，上游证书不受信任，链路：直连", await proxy.Logs());
    }

    [Theory]
    [InlineData("http", ClientType.Claude)]
    [InlineData("socks5", ClientType.Claude)]
    [InlineData("http", ClientType.Codex)]
    [InlineData("socks5", ClientType.Codex)]
    public async Task TunnelsThroughSystemProxy(string scheme, ClientType client)
    {
        await using var upstream = await HttpsUpstream.StartAsync(context => Upstream.Json(context, 200, $"{{\"host\":\"{context.Request.Host.Value}\"}}"));
        // Claude 指纹连接器自己建隧道并带代理账号；Codex 走 HttpClient 自带的代理逻辑，这里用不需要账号的代理（与本机常见的 127.0.0.1:7897 一致）。
        var withAccount = client == ClientType.Claude;
        await using var tunnel = await TunnelProxy.StartAsync(scheme, withAccount ? "user" : string.Empty, withAccount ? "p@ss" : string.Empty);
        var account = withAccount ? "user:p%40ss@" : string.Empty;
        var resolver = new FixedProxyResolver(new Uri($"{scheme}://{account}127.0.0.1:{tunnel.Port}"));
        await using var proxy = await TlsProxy.StartAsync(upstream.BaseUrl, upstream.Certificate, resolver, client);
        using var http = TestClient.Create(10);
        using var response = await TestClient.Send(http, HttpMethod.Post, $"{proxy.Address}/v1/messages", "{}", "application/json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"{{\"host\":\"localhost:{upstream.Port}\"}}", await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { $"localhost:{upstream.Port}" }, tunnel.Targets.ToArray());
    }

    [Fact]
    public async Task ReportsRejectedProxyTunnel()
    {
        await using var tunnel = await TunnelProxy.StartAsync("http", "user", "right");
        var resolver = new FixedProxyResolver(new Uri($"http://user:wrong@127.0.0.1:{tunnel.Port}"));
        await using var proxy = await TlsProxy.StartAsync("https://localhost:1", null, resolver);
        using var http = TestClient.Create(10);
        using var response = await TestClient.Send(http, HttpMethod.Post, $"{proxy.Address}/v1/messages", "{}", "application/json");
        Assert.False(response.IsSuccessStatusCode);
        Assert.Contains("连不上上游，代理要求认证（HTTP 407），链路：系统代理", await proxy.Logs());
    }

    [Fact]
    public async Task SendsRequestHeadersInClientOrderOverFingerprintConnection()
    {
        await using var upstream = await RawHttpsUpstream.StartAsync();
        await using var proxy = await TlsProxy.StartAsync(upstream.BaseUrl, upstream.Certificate);
        var port = new Uri(proxy.Address).Port;
        // Claude Code 2.1.283 直连时的请求头顺序（抓包所得，值做了简化）。
        var clientOrder = new[]
        {
            "Accept: application/json",
            "Authorization: Bearer sk-order-test",
            "Content-Type: application/json",
            "User-Agent: claude-cli/2.1.283 (external, cli)",
            "X-Stainless-Lang: js",
            "anthropic-version: 2023-06-01",
            "x-app: cli",
            "Connection: keep-alive",
            $"Host: 127.0.0.1:{port}",
            "Accept-Encoding: gzip, deflate, br, zstd",
            "Content-Length: 2",
        };
        // 同一条连接上先发一个不经转发的健康检查（会留下一条用不上的记录），再发两个转发请求；
        // 第二个请求头名全小写，检验大小写也按客户端原样转发。
        var modelsOrder = new[] { $"host: 127.0.0.1:{port}", "user-agent: claude-cli/2.1.283", "x-app: cli", "accept: */*" };
        var heads = await RawHttp.ExchangeAsync(port,
            $"GET /_retry/health HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n\r\n",
            "POST /v1/messages?beta=true HTTP/1.1\r\n" + string.Join("\r\n", clientOrder) + "\r\n\r\n{}",
            "GET /v1/models HTTP/1.1\r\n" + string.Join("\r\n", modelsOrder) + "\r\n\r\n");
        Assert.All(heads, head => Assert.StartsWith("HTTP/1.1 200", head));

        var received = upstream.Heads.Select(head => head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)).ToArray();
        Assert.Equal(2, received.Length);
        Assert.Equal("POST /v1/messages?beta=true HTTP/1.1", received[0][0]);
        Assert.Equal("GET /v1/models HTTP/1.1", received[1][0]);
        Assert.All(received, lines => Assert.DoesNotContain(lines, line => line.StartsWith(HeaderOrderStream.PlanHeader, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(Names(clientOrder), Names(received[0].Skip(1)));
        Assert.Equal(Names(modelsOrder), Names(received[1].Skip(1)));
        Assert.Contains($"Host: localhost:{upstream.Port}", received[0]);
        Assert.Contains($"host: localhost:{upstream.Port}", received[1]);

        static string Names(IEnumerable<string> lines) => string.Join(", ", lines.Select(line => line[..line.IndexOf(':')]));
    }

    [Fact]
    public async Task SendsRequestHeadersInClientOrderOverCodexConnection()
    {
        await using var upstream = await RawHttpsUpstream.StartAsync();
        await using var proxy = await TlsProxy.StartAsync(upstream.BaseUrl, upstream.Certificate, client: ClientType.Codex);
        var port = new Uri(proxy.Address).Port;
        // Codex 0.154 直连时的请求头顺序（值做了简化）；Codex 不发 Accept-Encoding，代理也不补。
        var clientOrder = new[]
        {
            "authorization: Bearer sk-order-test",
            "originator: codex_cli_rs",
            "user-agent: codex_cli_rs/0.154.0 (Windows 10.0.19045; x86_64)",
            "session_id: 0199-order",
            "accept: text/event-stream",
            "content-type: application/json",
            $"host: 127.0.0.1:{port}",
            "content-length: 2",
        };
        var heads = await RawHttp.ExchangeAsync(port, "POST /v1/responses HTTP/1.1\r\n" + string.Join("\r\n", clientOrder) + "\r\n\r\n{}");
        Assert.All(heads, head => Assert.StartsWith("HTTP/1.1 200", head));

        var received = upstream.Heads.Single().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("POST /v1/responses HTTP/1.1", received[0]);
        Assert.DoesNotContain(received, line => line.StartsWith(HeaderOrderStream.PlanHeader, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Names(clientOrder), Names(received.Skip(1)));
        Assert.Contains($"host: localhost:{upstream.Port}", received);

        static string Names(IEnumerable<string> lines) => string.Join(", ", lines.Select(line => line[..line.IndexOf(':')]));
    }

    [Fact]
    public void LegacyFingerprintSwitchInConfigIsIgnoredAndDropped()
    {
        // 早先版本在通道上保存过 claude_tls_fingerprint 开关；现在读取时忽略，保存时不再写出。
        var builtin = ProxyConfig.Builtin();
        var json = System.Text.Json.Nodes.JsonNode.Parse(ProxyConfigJson.ToCanonicalJson(builtin))!;
        foreach (var route in json["routes"]!.AsArray())
        {
            route!["claude_tls_fingerprint"] = false;
        }

        var (loaded, _) = ProxyConfigJson.Parse(json.ToJsonString());
        Assert.Equal(builtin.Routes, loaded.Routes);
        Assert.DoesNotContain("claude_tls_fingerprint", ProxyConfigJson.ToCanonicalJson(loaded), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ 辅助

    private static string TempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "retry-proxy-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static CliCommand FixtureCli()
    {
        var command = new CliCommand("powershell.exe");
        command.Arguments.AddRange(new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(AppContext.BaseDirectory, "fixtures", "tls-capture-claude.ps1") });
        return command;
    }

    /// <summary>结构合法、可复现但扩展顺序与内置不同的指纹（交换 signature_algorithms 与 sct）。</summary>
    private static ClientHelloFingerprint Reordered()
    {
        var (head, extensions) = Split(TlsFingerprintStore.Builtin.Record);
        var signatures = extensions.FindIndex(item => item.Type == 13);
        var sct = extensions.FindIndex(item => item.Type == 18);
        (extensions[signatures], extensions[sct]) = (extensions[sct], extensions[signatures]);
        return ClientHelloFingerprint.Parse(Join(head, extensions));
    }

    /// <summary>拆成“扩展之前的 ClientHello 正文”和扩展列表。</summary>
    private static (byte[] Head, List<(int Type, byte[] Data)> Extensions) Split(byte[] record)
    {
        var body = record.AsSpan(9);
        var p = 2 + 32;
        p += 1 + body[p];
        p += 2 + ((body[p] << 8) | body[p + 1]);
        p += 1 + body[p];
        var head = body[..p].ToArray();
        var end = p + 2 + ((body[p] << 8) | body[p + 1]);
        p += 2;
        var extensions = new List<(int, byte[])>();
        while (p < end)
        {
            var type = (body[p] << 8) | body[p + 1];
            var length = (body[p + 2] << 8) | body[p + 3];
            extensions.Add((type, body.Slice(p + 4, length).ToArray()));
            p += 4 + length;
        }

        return (head, extensions);
    }

    private static byte[] Join(byte[] head, IEnumerable<(int Type, byte[] Data)> extensions)
    {
        var encoded = new MemoryStream();
        foreach (var (type, data) in extensions)
        {
            encoded.Write(new[] { (byte)(type >> 8), (byte)type, (byte)(data.Length >> 8), (byte)data.Length });
            encoded.Write(data);
        }

        var body = new MemoryStream();
        body.Write(head);
        body.Write(new[] { (byte)(encoded.Length >> 8), (byte)encoded.Length });
        encoded.WriteTo(body);
        var bodyLength = (int)body.Length;
        var record = new MemoryStream();
        record.Write(new byte[] { 0x16, 0x03, 0x01, (byte)((bodyLength + 4) >> 8), (byte)(bodyLength + 4), 0x01, (byte)(bodyLength >> 16), (byte)(bodyLength >> 8), (byte)bodyLength });
        body.WriteTo(record);
        return record.ToArray();
    }

    private sealed class FixedProxyResolver(Uri proxyUrl) : IProxyResolver
    {
        public ProxyDecision Resolve(Uri target) => new(proxyUrl, true);
    }

    /// <summary>https 上游的 Claude（TLS 指纹）或 Codex 通道，不重试；指纹来源为内置、不抓取。</summary>
    private sealed class TlsProxy : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancel;
        private readonly ProxyHost _host;
        private readonly ProxyLogger _logger;
        private readonly string _logDirectory;

        private TlsProxy(ProxyHost host, Core.Proxy.RetryProxy proxy, ProxyLogger logger, string logDirectory, CancellationTokenSource cancel)
        {
            _host = host;
            Proxy = proxy;
            _logger = logger;
            _logDirectory = logDirectory;
            _cancel = cancel;
            Address = $"http://127.0.0.1:{host.Port}";
        }

        public string Address { get; }

        public Core.Proxy.RetryProxy Proxy { get; }

        public static async Task<TlsProxy> StartAsync(string upstream, X509Certificate2? trustedRoot, IProxyResolver? resolver = null, ClientType client = ClientType.Claude)
        {
            var logDirectory = TempDirectory();
            var logger = ProxyLogger.Silent(logDirectory);
            var config = new ProxyConfig
            {
                ClientType = client,
                UpstreamBaseUrl = upstream,
                ListenPort = 18080,
                MaxRetries = 0,
                TimeoutSeconds = 8,
            };
            var cancel = new CancellationTokenSource();
            var proxy = new Core.Proxy.RetryProxy(config, logger, new ProxyMetrics(), cancel.Token, resolver)
                .WithTlsFingerprint(new TlsFingerprintStore(_ => throw new InvalidOperationException()), trustedRoot);
            if (trustedRoot is not null)
            {
                proxy.WithTrustedTestCertificate(trustedRoot);
            }

            var host = await ProxyHost.StartAsync(proxy, 0);
            return new TlsProxy(host, proxy, logger, logDirectory, cancel);
        }

        public async Task<string> Logs()
        {
            await TestClock.WaitUntil(() => Proxy.Metrics.Snapshot().ActiveRequests == 0);
            return LogFiles.ReadAll(_logDirectory);
        }

        public async ValueTask DisposeAsync()
        {
            _cancel.Cancel();
            await _host.StopAsync(TimeSpan.FromMilliseconds(200));
            await _host.DisposeAsync();
            _logger.Dispose();
            try
            {
                Directory.Delete(_logDirectory, true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>本机 HTTPS 假上游：自签 localhost 证书，只监听 127.0.0.1。</summary>
    private sealed class HttpsUpstream : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private HttpsUpstream(WebApplication app, int port, X509Certificate2 certificate)
        {
            _app = app;
            Port = port;
            Certificate = certificate;
        }

        public int Port { get; }

        public string BaseUrl => $"https://localhost:{Port}";

        public X509Certificate2 Certificate { get; }

        public static async Task<HttpsUpstream> StartAsync(Func<HttpContext, Task> handler)
        {
            var certificate = CreateCertificate();
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
            builder.Logging.ClearProviders();
            builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromMilliseconds(100));
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.AddServerHeader = false;
                options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate));
            });
            var app = builder.Build();
            app.Run(new RequestDelegate(handler));
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return new HttpsUpstream(app, new Uri(address).Port, certificate);
        }

        internal static X509Certificate2 CreateCertificate()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
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
            Certificate.Dispose();
        }
    }

    /// <summary>
    /// 记录原始请求字节的本机 HTTPS 假上游：Kestrel 会重排请求头，这里直接读 TLS 解密后的请求头原文。
    /// 每个请求都回 200 <c>{}</c>，连接保持复用。
    /// </summary>
    private sealed class RawHttpsUpstream : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cancel = new();
        private readonly Task _loop;

        private RawHttpsUpstream()
        {
            Certificate = HttpsUpstream.CreateCertificate();
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _loop = Task.Run(AcceptLoopAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public string BaseUrl => $"https://localhost:{Port}";

        public X509Certificate2 Certificate { get; }

        /// <summary>收到的请求头原文（不含正文），按到达顺序。</summary>
        public ConcurrentQueue<string> Heads { get; } = new();

        public static Task<RawHttpsUpstream> StartAsync() => Task.FromResult(new RawHttpsUpstream());

        private async Task AcceptLoopAsync()
        {
            while (!_cancel.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cancel.Token);
                }
                catch (Exception)
                {
                    return;
                }

                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var _ = client;
            try
            {
                await using var tls = new System.Net.Security.SslStream(client.GetStream());
                await tls.AuthenticateAsServerAsync(Certificate);
                while (!_cancel.IsCancellationRequested)
                {
                    var head = await RawHttp.ReadHeadAsync(tls, _cancel.Token);
                    if (head is null)
                    {
                        return;
                    }

                    Heads.Enqueue(head);
                    await RawHttp.ReadBodyAsync(tls, head, _cancel.Token);
                    await tls.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\n\r\n{}"), _cancel.Token);
                }
            }
            catch (Exception)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cancel.Cancel();
            _listener.Stop();
            try
            {
                await _loop;
            }
            catch (Exception)
            {
            }

            Certificate.Dispose();
        }
    }

    /// <summary>按原始字节收发 HTTP/1.1：用来构造 HttpClient 无法控制头顺序的请求，并读取对端收到的原文。</summary>
    private static class RawHttp
    {
        /// <summary>在同一条连接上依次发送请求，返回每个响应头原文。</summary>
        public static async Task<List<string>> ExchangeAsync(int port, params string[] requests)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var stream = client.GetStream();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var heads = new List<string>();
            foreach (var request in requests)
            {
                await stream.WriteAsync(Encoding.Latin1.GetBytes(request), timeout.Token);
                var head = await ReadHeadAsync(stream, timeout.Token) ?? throw new IOException("代理没有返回响应");
                await ReadBodyAsync(stream, head, timeout.Token);
                heads.Add(head);
            }

            return heads;
        }

        /// <summary>读到空行为止，返回请求头 / 响应头原文；对端在开头就关闭连接时返回 null。</summary>
        public static async Task<string?> ReadHeadAsync(Stream stream, CancellationToken cancellationToken)
        {
            var head = new StringBuilder();
            var one = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await stream.ReadAsync(one, cancellationToken) == 0)
                {
                    return head.Length == 0 ? null : throw new IOException("请求头中途断开");
                }

                head.Append((char)one[0]);
            }

            return head.ToString();
        }

        public static async Task ReadBodyAsync(Stream stream, string head, CancellationToken cancellationToken)
        {
            if (head.Contains("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase))
            {
                // 逐块读：长度行 → 数据 + CRLF；长度 0 之后读到空行结束（测试里没有尾部字段）。
                while (true)
                {
                    var sizeLine = await ReadLineAsync(stream, cancellationToken);
                    var size = Convert.ToInt32(sizeLine.Split(';')[0].Trim(), 16);
                    if (size == 0)
                    {
                        await ReadLineAsync(stream, cancellationToken);
                        return;
                    }

                    await stream.ReadExactlyAsync(new byte[size + 2], cancellationToken);
                }
            }

            var line = head.Split("\r\n").FirstOrDefault(item => item.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            if (line is not null && int.TryParse(line["Content-Length:".Length..].Trim(), out var length) && length > 0)
            {
                await stream.ReadExactlyAsync(new byte[length], cancellationToken);
            }
        }

        private static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
        {
            var line = new StringBuilder();
            var one = new byte[1];
            while (!line.ToString().EndsWith("\r\n", StringComparison.Ordinal))
            {
                await stream.ReadExactlyAsync(one, cancellationToken);
                line.Append((char)one[0]);
            }

            return line.ToString(0, line.Length - 2);
        }
    }

    /// <summary>带用户名密码认证的 HTTP CONNECT / SOCKS5 隧道代理，把目标 localhost 连到 127.0.0.1。</summary>
    private sealed class TunnelProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly string _scheme;
        private readonly string _user;
        private readonly string _password;
        private readonly CancellationTokenSource _cancel = new();
        private readonly Task _loop;

        private TunnelProxy(string scheme, string user, string password)
        {
            _scheme = scheme;
            _user = user;
            _password = password;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _loop = Task.Run(AcceptLoopAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public ConcurrentQueue<string> Targets { get; } = new();

        public static Task<TunnelProxy> StartAsync(string scheme, string user, string password) => Task.FromResult(new TunnelProxy(scheme, user, password));

        private async Task AcceptLoopAsync()
        {
            while (!_cancel.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cancel.Token);
                }
                catch (Exception)
                {
                    return;
                }

                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var target = _scheme == "http" ? await HttpHandshakeAsync(stream) : await Socks5HandshakeAsync(stream);
                if (target is null)
                {
                    return;
                }

                Targets.Enqueue($"{target.Value.Host}:{target.Value.Port}");
                using var upstream = new TcpClient();
                await upstream.ConnectAsync(IPAddress.Loopback, target.Value.Port);
                var upstreamStream = upstream.GetStream();
                await Task.WhenAny(stream.CopyToAsync(upstreamStream), upstreamStream.CopyToAsync(stream));
            }
        }

        private async Task<(string Host, int Port)?> HttpHandshakeAsync(NetworkStream stream)
        {
            var head = new StringBuilder();
            var one = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await stream.ReadAsync(one) == 0)
                {
                    return null;
                }

                head.Append((char)one[0]);
            }

            var lines = head.ToString().Split("\r\n");
            var expected = "Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_user}:{_password}"));
            if (_user.Length > 0 && !lines.Contains(expected))
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 407 Proxy Authentication Required\r\nContent-Length: 0\r\n\r\n"));
                return null;
            }

            var authority = lines[0].Split(' ')[1];
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"));
            var separator = authority.LastIndexOf(':');
            return (authority[..separator], int.Parse(authority[(separator + 1)..]));
        }

        private async Task<(string Host, int Port)?> Socks5HandshakeAsync(NetworkStream stream)
        {
            var greeting = new byte[2];
            await stream.ReadExactlyAsync(greeting);
            var methods = new byte[greeting[1]];
            await stream.ReadExactlyAsync(methods);
            if (_user.Length == 0 && methods.Contains((byte)0))
            {
                await stream.WriteAsync(new byte[] { 5, 0 });
                return await Socks5ConnectRequestAsync(stream);
            }

            if (!methods.Contains((byte)2))
            {
                await stream.WriteAsync(new byte[] { 5, 0xff });
                return null;
            }

            await stream.WriteAsync(new byte[] { 5, 2 });
            var version = new byte[2];
            await stream.ReadExactlyAsync(version);
            var user = new byte[version[1]];
            await stream.ReadExactlyAsync(user);
            var passwordLength = new byte[1];
            await stream.ReadExactlyAsync(passwordLength);
            var password = new byte[passwordLength[0]];
            await stream.ReadExactlyAsync(password);
            var ok = Encoding.UTF8.GetString(user) == _user && Encoding.UTF8.GetString(password) == _password;
            await stream.WriteAsync(new byte[] { 1, (byte)(ok ? 0 : 1) });
            if (!ok)
            {
                return null;
            }

            return await Socks5ConnectRequestAsync(stream);
        }

        private static async Task<(string Host, int Port)?> Socks5ConnectRequestAsync(NetworkStream stream)
        {
            var request = new byte[5];
            await stream.ReadExactlyAsync(request);
            if (request[3] != 3)
            {
                return null;
            }

            var name = new byte[request[4]];
            await stream.ReadExactlyAsync(name);
            var port = new byte[2];
            await stream.ReadExactlyAsync(port);
            await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 });
            return (Encoding.ASCII.GetString(name), (port[0] << 8) | port[1]);
        }

        public async ValueTask DisposeAsync()
        {
            _cancel.Cancel();
            _listener.Stop();
            try
            {
                await _loop;
            }
            catch (Exception)
            {
            }
        }
    }
}
