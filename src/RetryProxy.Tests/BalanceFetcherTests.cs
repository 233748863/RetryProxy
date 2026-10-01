using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RetryProxy.Core.Balance;
using RetryProxy.Core.Config;
using RetryProxy.Core.Service;
using Xunit;

namespace RetryProxy.Tests;

public sealed class BalanceFetcherTests
{
    private const string Key = "sk-private-fixture";
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public bool Disposed { get; private set; }
        public List<string> Paths { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            return send(request, cancellationToken);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private static HttpResponseMessage Json(string text, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(text, Encoding.UTF8, "application/json"),
    };

    private static async Task<BalanceResult> Fetch(string text, BalanceQueryMode mode = BalanceQueryMode.Usage, string key = Key)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(text)));
        using var client = new HttpClient(handler);
        using var fetcher = new BalanceFetcher(client, "1.2.3");
        return await fetcher.FetchAsync("https://fixture.invalid/v1", key, new BalanceQuery { Mode = mode });
    }

    [Theory]
    [InlineData("{\"remaining\":12.34}", "12.34", "USD")]
    [InlineData("{\"remaining\":\"1.25e2\",\"unit\":\"EUR\"}", "125", "EUR")]
    [InlineData("{\"quota\":{\"remaining\":\"8.75\",\"unit\":\"积分\"}}", "8.75", "积分")]
    [InlineData("{\"balance\":0}", "0", "USD")]
    [InlineData("{\"balance\":-3.5}", "-3.5", "USD")]
    [InlineData("{\"remaining\":1,\"quota\":{\"remaining\":2,\"unit\":\"CNY\"},\"balance\":3,\"unit\":\"EUR\"}", "1", "EUR")]
    [InlineData("{\"remaining\":null,\"quota\":{\"remaining\":2},\"balance\":3}", "2", "USD")]
    [InlineData("{\"remaining\":\"invalid\",\"balance\":7}", "7", "USD")]
    public async Task Usage_resolves_fields_in_priority_order(string json, string amount, string unit)
    {
        var result = await Fetch(json);
        Assert.Equal(decimal.Parse(amount, CultureInfo.InvariantCulture), result.Amount);
        Assert.Equal(unit, result.Unit);
        Assert.Null(result.Error);
        Assert.Equal(BalanceQueryMode.Usage, result.DetectedMode);
    }

    [Theory]
    [InlineData("{\"remaining\":\"10\"}", "10", "CNY")]
    [InlineData("{\"balance\":12,\"currency\":\"EUR\"}", "12", "EUR")]
    [InlineData("{\"data\":{\"remaining\":2,\"balance\":3,\"unit\":\"USD\",\"currency\":\"EUR\"},\"remaining\":9}", "2", "USD")]
    [InlineData("{\"data\":{\"balance\":\"-4.125\"}}", "-4.125", "CNY")]
    [InlineData("{\"data\":false,\"balance\":6}", "6", "CNY")]
    public async Task User_balance_uses_data_object_and_currency_fallback(string json, string amount, string unit)
    {
        var result = await Fetch(json, BalanceQueryMode.UserBalance);
        Assert.Equal(decimal.Parse(amount, CultureInfo.InvariantCulture), result.Amount);
        Assert.Equal(unit, result.Unit);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData(BalanceQueryMode.Usage, "{\"is_active\":false}")]
    [InlineData(BalanceQueryMode.Usage, "{\"isActive\":false}")]
    [InlineData(BalanceQueryMode.Usage, "{\"data\":{\"is_active\":false}}")]
    [InlineData(BalanceQueryMode.UserBalance, "{\"is_active\":false,\"data\":{\"balance\":12}}")]
    [InlineData(BalanceQueryMode.UserBalance, "{\"data\":{\"isActive\":false}}")]
    [InlineData(BalanceQueryMode.OpenAiBilling, "{\"is_active\":false}")]
    public async Task False_active_marker_does_not_require_amount(BalanceQueryMode mode, string json)
    {
        var result = await Fetch(json, mode);
        Assert.True(result.IsInvalid);
        Assert.Null(result.Amount);
        Assert.Null(result.Error);
        Assert.Equal(mode, result.DetectedMode);
    }

    [Theory]
    [InlineData("{\"is_active\":true,\"remaining\":1}")]
    [InlineData("{\"is_active\":\"false\",\"remaining\":1}")]
    [InlineData("{\"is_active\":0,\"remaining\":1}")]
    public async Task Only_boolean_false_marks_invalid(string json)
    {
        var result = await Fetch(json);
        Assert.False(result.IsInvalid);
        Assert.Equal(1m, result.Amount);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"remaining\":\"NaN\"}")]
    [InlineData("{\"remaining\":\"Infinity\"}")]
    [InlineData("{\"remaining\":\"1,200\"}")]
    [InlineData("{\"remaining\":1e100}")]
    [InlineData("{\"remaining\":true}")]
    [InlineData("{\"remaining\":[]}")]
    [InlineData("{\"remaining\":\"79228162514264337593543950336\"}")]
    public async Task Missing_or_invalid_numeric_value_does_not_become_zero(string json)
    {
        var result = await Fetch(json);
        Assert.Null(result.Amount);
        Assert.NotNull(result.Error);
        Assert.Null(result.DetectedMode);
    }

    [Fact]
    public async Task Data_object_without_amount_does_not_fall_back_to_root_balance()
    {
        var result = await Fetch("{\"data\":{},\"balance\":8}", BalanceQueryMode.UserBalance);
        Assert.NotNull(result.Error);
    }

    [Theory]
    [InlineData("abcdefghijklmnopq")]
    [InlineData("USD\nsecret")]
    [InlineData("USD\u202Esecret")]
    [InlineData("<script>")]
    [InlineData("sk-private-fixture")]
    [InlineData("https://evil")]
    public async Task External_unit_is_bounded_and_sanitized(string unit)
    {
        var result = await Fetch(JsonSerializer.Serialize(new { remaining = 2, unit }));
        Assert.Equal("USD", result.Unit);
        Assert.DoesNotContain(Key, result.ToString());
    }

    [Fact]
    public async Task Unsafe_primary_unit_uses_safe_quota_unit()
    {
        var result = await Fetch("{\"remaining\":2,\"unit\":\"bad\\nunit\",\"quota\":{\"unit\":\"积分\"}}");
        Assert.Equal("积分", result.Unit);
    }

    [Fact]
    public async Task Unit_matching_key_exactly_is_hidden_even_when_default_unit_matches()
    {
        var result = await Fetch("{\"remaining\":2,\"unit\":\"USD\"}", key: "USD");
        Assert.Equal(string.Empty, result.Unit);
        Assert.DoesNotContain("USD", result.ToString());
    }

    [Theory]
    [InlineData("10", "123", "8.77")]
    [InlineData("0", "0", "0")]
    [InlineData("1", "250", "-1.5")]
    [InlineData("0.1", "5", "0.05")]
    public async Task Billing_converts_usage_cents_before_subtracting(string limit, string used, string expected)
    {
        using var handler = new Handler((request, _) => Task.FromResult(Json(request.RequestUri!.AbsolutePath.EndsWith("subscription", StringComparison.Ordinal)
            ? $"{{\"hard_limit_usd\":\"{limit}\"}}" : $"{{\"total_usage\":\"{used}\"}}")));
        using var client = new HttpClient(handler);
        using var fetcher = new BalanceFetcher(client, "1.0");
        var result = await fetcher.FetchAsync("https://fixture.invalid", Key, new BalanceQuery { Mode = BalanceQueryMode.OpenAiBilling });
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), result.Amount);
        Assert.Equal("USD", result.Unit);
        Assert.Equal(new[] { "/v1/dashboard/billing/subscription", "/v1/dashboard/billing/usage" }, handler.Paths);
    }

    [Theory]
    [InlineData("100000000")]
    [InlineData("100000001")]
    public async Task Unlimited_billing_does_not_need_usage_endpoint(string limit)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json($"{{\"hard_limit_usd\":{limit}}}")));
        using var client = new HttpClient(handler);
        using var fetcher = new BalanceFetcher(client, "1.0");
        var result = await fetcher.FetchAsync("https://fixture.invalid/v1/", Key, new BalanceQuery { Mode = BalanceQueryMode.OpenAiBilling });
        Assert.True(result.IsUnlimited);
        Assert.Null(result.Amount);
        Assert.Null(result.Error);
        Assert.Single(handler.Paths);
    }

    [Theory]
    [InlineData("{\"total_usage\":null}")]
    [InlineData("{\"total_usage\":\"Infinity\"}")]
    [InlineData("{}")]
    public async Task Billing_requires_valid_usage_for_finite_limit(string usage)
    {
        using var handler = new Handler((request, _) => Task.FromResult(Json(request.RequestUri!.AbsolutePath.EndsWith("subscription", StringComparison.Ordinal)
            ? "{\"hard_limit_usd\":10}" : usage)));
        using var client = new HttpClient(handler);
        using var fetcher = new BalanceFetcher(client, "1");
        var result = await fetcher.FetchAsync("https://fixture.invalid", Key, new BalanceQuery { Mode = BalanceQueryMode.OpenAiBilling });
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Billing_usage_can_mark_key_invalid()
    {
        using var handler = new Handler((request, _) => Task.FromResult(Json(request.RequestUri!.AbsolutePath.EndsWith("subscription", StringComparison.Ordinal)
            ? "{\"hard_limit_usd\":10}" : "{\"data\":{\"isActive\":false}}")));
        using var client = new HttpClient(handler);
        using var fetcher = new BalanceFetcher(client, "1");
        var result = await fetcher.FetchAsync("https://fixture.invalid", Key, new BalanceQuery { Mode = BalanceQueryMode.OpenAiBilling });
        Assert.True(result.IsInvalid);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Billing_overflow_is_a_safe_failure()
    {
        using var handler = new Handler((request, _) => Task.FromResult(Json(request.RequestUri!.AbsolutePath.EndsWith("subscription", StringComparison.Ordinal)
            ? "{\"hard_limit_usd\":-79228162514264337593543950335}" : "{\"total_usage\":79228162514264337593543950335}")));
        using var client = new HttpClient(handler);
        using var fetcher = new BalanceFetcher(client, "1");
        var result = await fetcher.FetchAsync("https://fixture.invalid", Key, new BalanceQuery { Mode = BalanceQueryMode.OpenAiBilling });
        Assert.Equal("余额数值超出支持范围", result.Error);
    }

    [Theory]
    [InlineData("https://fixture.invalid", "/v1/usage")]
    [InlineData("https://fixture.invalid/v1", "/v1/usage")]
    [InlineData("https://fixture.invalid/prefix/v1/", "/prefix/v1/usage")]
    [InlineData("https://fixture.invalid/prefix", "/prefix/v1/usage")]
    public async Task Requests_use_expected_path_authentication_and_version(string baseUrl, string path)
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(Key, request.Headers.Authorization.Parameter);
            Assert.Equal("RetryProxy/1.2.3-beta", request.Headers.UserAgent.ToString());
            Assert.Equal("identity", request.Headers.AcceptEncoding.ToString());
            Assert.Null(request.Content);
            Assert.False(request.Headers.Contains("x-api-key"));
            return Task.FromResult(Json("{\"remaining\":1}"));
        });
        using var client = new HttpClient(handler);
        using var fetcher = new BalanceFetcher(client, "1.2.3-beta");
        Assert.Null((await fetcher.FetchAsync(baseUrl, Key, new BalanceQuery { Mode = BalanceQueryMode.Usage })).Error);
        Assert.Equal(path, Assert.Single(handler.Paths));
    }

    [Fact]
    public async Task Auto_remembers_successful_mode_without_mutating_input()
    {
        using var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/v1/usage"
            ? Json("{}", HttpStatusCode.NotFound) : Json("{\"data\":{\"balance\":2}}")));
        using var client = new HttpClient(handler);
        using var fetcher = new BalanceFetcher(client, "1");
        var query = new BalanceQuery();
        var result = await fetcher.FetchAsync("https://fixture.invalid", Key, query);
        Assert.Equal(BalanceQueryMode.UserBalance, result.DetectedMode);
        Assert.Null(query.Detected);
        Assert.Equal(new[] { "/v1/usage", "/user/balance" }, handler.Paths);
    }

    [Fact]
    public async Task Auto_tries_detected_mode_first_and_falls_back_without_repeating_it()
    {
        using var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/user/balance"
            ? Json("{}", HttpStatusCode.NotFound) : Json("{\"remaining\":2}")));
        using var client = new HttpClient(handler);
        using var fetcher = new BalanceFetcher(client, "1");
        var result = await fetcher.FetchAsync("https://fixture.invalid", Key, new BalanceQuery { Detected = BalanceQueryMode.UserBalance });
        Assert.Equal(BalanceQueryMode.Usage, result.DetectedMode);
        Assert.Equal(new[] { "/user/balance", "/v1/usage" }, handler.Paths);
    }

    [Fact]
    public async Task Auto_falls_back_through_all_three_formats()
    {
        using var handler = new Handler((request, _) => Task.FromResult(Json(request.RequestUri!.AbsolutePath switch
        {
            "/v1/dashboard/billing/subscription" => "{\"hard_limit_usd\":10}",
            "/v1/dashboard/billing/usage" => "{\"total_usage\":100}",
            _ => "{}",
        })));
        using var client = new HttpClient(handler);
        using var fetcher = new BalanceFetcher(client, "1");
        var result = await fetcher.FetchAsync("https://fixture.invalid", Key, new BalanceQuery());
        Assert.Equal(BalanceQueryMode.OpenAiBilling, result.DetectedMode);
        Assert.Equal(9m, result.Amount);
        Assert.Equal(4, handler.Paths.Count);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(301)]
    [InlineData(307)]
    public async Task Auth_rate_limit_and_redirect_stop_auto_detection_without_reading_body(int status)
    {
        var content = new ThrowOnReadContent();
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = content,
            Headers = { Location = new Uri("https://other.invalid/secret") },
        }));
        using var client = new HttpClient(handler);
        using var fetcher = new BalanceFetcher(client, "1");
        var result = await fetcher.FetchAsync("https://fixture.invalid", Key, new BalanceQuery());
        Assert.Equal($"余额查询失败：服务商返回 HTTP {status}", result.Error);
        Assert.Single(handler.Paths);
        Assert.False(content.ReadAttempted);
        Assert.Null(result.DetectedMode);
    }

    [Fact]
    public async Task None_performs_no_network_request()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Must not send"));
        using var client = new HttpClient(handler);
        using var fetcher = new BalanceFetcher(client, "1");
        var result = await fetcher.FetchAsync("not-a-url", "", new BalanceQuery { Mode = BalanceQueryMode.None });
        Assert.Null(result.Error);
        Assert.Null(result.Amount);
        Assert.Empty(handler.Paths);
    }

    [Theory]
    [InlineData("not-url", Key)]
    [InlineData("file:///private", Key)]
    [InlineData("https://user:password@fixture.invalid", Key)]
    [InlineData("https://fixture.invalid?secret=yes", Key)]
    [InlineData("https://fixture.invalid#secret", Key)]
    [InlineData("https://fixture.invalid", "")]
    [InlineData("https://fixture.invalid", "secret\r\ninjected: yes")]
    public async Task Invalid_request_configuration_is_rejected_before_network(string baseUrl, string key)
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Must not send"));
        using var client = new HttpClient(handler);
        using var fetcher = new BalanceFetcher(client, "1");
        var result = await fetcher.FetchAsync(baseUrl, key, new BalanceQuery());
        Assert.NotNull(result.Error);
        Assert.Empty(handler.Paths);
        Assert.DoesNotContain("password", result.ToString());
    }

    [Fact]
    public async Task Json_and_transport_errors_never_expose_original_text()
    {
        var invalid = await Fetch($"not json {Key}");
        Assert.Equal("余额查询结果格式无效", invalid.Error);
        using var handler = new Handler((_, _) => throw new HttpRequestException("network " + Key));
        using var client = new HttpClient(handler);
        using var fetcher = new BalanceFetcher(client, "1");
        var failed = await fetcher.FetchAsync("https://fixture.invalid", Key, new BalanceQuery());
        Assert.Equal("余额查询失败，请检查网络连接", failed.Error);
        Assert.DoesNotContain(Key, invalid.ToString() + failed);
    }

    [Fact]
    public async Task Caller_cancellation_is_propagated()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new Handler(async (_, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json("{}");
        });
        using var client = new HttpClient(handler);
        using var fetcher = new BalanceFetcher(client, "1");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetcher.FetchAsync("https://fixture.invalid", Key, new BalanceQuery(), cancellation.Token));
    }

    [Fact]
    public async Task Whole_auto_query_has_one_injected_ten_second_deadline()
    {
        var clock = new DeadlineClock();
        using var handler = new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/usage") return Json("{}", HttpStatusCode.NotFound);
            clock.Fire();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json("{}");
        });
        using var client = new HttpClient(handler);
        using var fetcher = new BalanceFetcher(client, "1", clock);
        var result = await fetcher.FetchAsync("https://fixture.invalid", Key, new BalanceQuery());
        Assert.Equal("余额查询超时，请稍后重试", result.Error);
        Assert.Equal(1, clock.Created);
        Assert.Equal(TimeSpan.FromSeconds(10), clock.Due);
        Assert.Equal(2, handler.Paths.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Oversized_response_is_rejected_with_or_without_content_length(bool knownLength)
    {
        var body = "{\"remaining\":1,\"padding\":\"" + new string('a', BalanceFetcher.MaxResponseBytes) + "\"}";
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = knownLength ? new StringContent(body) : new UnknownLengthContent(Encoding.UTF8.GetBytes(body)),
        }));
        using var client = new HttpClient(handler);
        using var fetcher = new BalanceFetcher(client, "1");
        var result = await fetcher.FetchAsync("https://fixture.invalid", Key, new BalanceQuery());
        Assert.Equal("余额查询结果超过 1 MiB，已停止读取", result.Error);
        Assert.Single(handler.Paths);
    }

    [Fact]
    public async Task Exactly_one_MiB_response_is_accepted()
    {
        const string prefix = "{\"remaining\":1,\"padding\":\"";
        const string suffix = "\"}";
        var body = prefix + new string('a', BalanceFetcher.MaxResponseBytes - prefix.Length - suffix.Length) + suffix;
        var result = await Fetch(body);
        Assert.Equal(1m, result.Amount);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Owned_transport_disables_redirects_and_cookies_and_uses_resolver()
    {
        var resolver = new FakeResolver();
        using var handler = BalanceFetcher.CreateHandler(resolver);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.True(handler.UseProxy);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.Equal(new Uri("http://127.0.0.1:7897"), handler.Proxy!.GetProxy(new Uri("https://fixture.invalid")));
        Assert.Equal(1, resolver.Calls);
    }

    [Fact]
    public async Task Disposing_fetcher_does_not_dispose_injected_http_client()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json("{\"remaining\":1}")));
        using var client = new HttpClient(handler);
        var fetcher = new BalanceFetcher(client, "1");
        fetcher.Dispose();
        Assert.False(handler.Disposed);
        using var response = await client.GetAsync("https://fixture.invalid");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fetcher.FetchAsync("https://fixture.invalid", Key, new BalanceQuery()));
    }

    private sealed class FakeResolver : IProxyResolver
    {
        public int Calls { get; private set; }
        public ProxyDecision? Resolve(Uri target) { Calls++; return new(new Uri("http://127.0.0.1:7897"), true); }
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes));
    }

    private sealed class ThrowOnReadContent : HttpContent
    {
        public bool ReadAttempted { get; private set; }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        { ReadAttempted = true; throw new IOException(Key); }
    }

    private sealed class DeadlineClock : TimeProvider
    {
        private DeadlineTimer? _timer;
        public int Created { get; private set; }
        public TimeSpan Due { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Created++;
            Due = dueTime;
            return _timer = new DeadlineTimer(callback, state);
        }
        public void Fire() => _timer!.Fire();
        private sealed class DeadlineTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
            public void Fire() { if (!_disposed) callback(state); }
        }
    }
}
