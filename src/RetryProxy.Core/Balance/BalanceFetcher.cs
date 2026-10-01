using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RetryProxy.Core.Config;
using RetryProxy.Core.Service;

namespace RetryProxy.Core.Balance;

/// <summary>
/// 读取三种余额接口，不接触客户端配置。整项查询（含自动识别和账单的两次请求）共用 10 秒期限，
/// 例如用量接口不支持后尝试余额接口，不会重新获得 10 秒。并发及结果缓存由调用方管理。
/// </summary>
public sealed class BalanceFetcher : IDisposable
{
    internal const int MaxResponseBytes = 1024 * 1024;
    internal static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);
    private static readonly BalanceQueryMode[] SupportedModes =
        [BalanceQueryMode.Usage, BalanceQueryMode.UserBalance, BalanceQueryMode.OpenAiBilling];
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly string _version;
    private readonly TimeProvider _clock;
    private bool _disposed;

    public BalanceFetcher(string version, IProxyResolver? proxyResolver = null)
        : this(new HttpClient(CreateHandler(proxyResolver ?? SystemProxyResolver.Shared))
        { Timeout = Timeout.InfiniteTimeSpan }, version)
    {
        _ownsClient = true;
    }

    internal BalanceFetcher(HttpClient client, string version, TimeProvider? timeProvider = null)
    {
        _client = client;
        _clock = timeProvider ?? TimeProvider.System;
        _version = version.Length is > 0 and <= 64
            && version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+') ? version : "0.0.0";
    }

    internal static SocketsHttpHandler CreateHandler(IProxyResolver resolver) => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        UseProxy = true,
        Proxy = new ResolverWebProxy(resolver),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(90),
    };

    public async Task<BalanceResult> FetchAsync(string baseUrl, string apiKey, BalanceQuery query,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var mode = query.Mode;
        var detected = query.Detected;
        if (mode == BalanceQueryMode.None) return new(null, string.Empty, false, false, null, null);
        if (!Enum.IsDefined(mode)) return Failure("余额查询方式无效");
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Any(char.IsControl)) return Failure("余额查询缺少有效 Key");
        string root;
        try
        {
            root = baseUrl.Trim().TrimEnd('/');
            UrlRules.ValidateBaseUrl(root, "供应商地址");
            if (root.EndsWith("/v1", StringComparison.Ordinal)) root = root[..^3];
        }
        catch (ConfigException) { return Failure("余额查询地址无效"); }

        using var deadline = new CancellationTokenSource(QueryTimeout, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            BalanceFailure? lastFailure = null;
            foreach (var candidate in Candidates(mode, detected))
            {
                try
                {
                    var result = await FetchModeAsync(root, apiKey, candidate, linked.Token).ConfigureAwait(false);
                    // 成功解析的接口才交给调用方记忆；本方法不修改调用方传入的设置对象。
                    return result with { DetectedMode = candidate };
                }
                catch (BalanceFailure error)
                {
                    lastFailure = error;
                    if (error.Terminal || mode != BalanceQueryMode.Auto) break;
                }
            }
            return Failure(lastFailure?.Message ?? "服务商未提供可用的余额接口");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure("余额查询超时，请稍后重试");
        }
        catch (HttpRequestException) { return Failure("余额查询失败，请检查网络连接"); }
        catch (IOException) { return Failure("无法读取余额查询结果"); }
    }

    private static IEnumerable<BalanceQueryMode> Candidates(BalanceQueryMode mode, BalanceQueryMode? detected)
    {
        if (mode != BalanceQueryMode.Auto) { yield return mode; yield break; }
        if (detected is { } known && SupportedModes.Contains(known)) yield return known;
        foreach (var candidate in SupportedModes)
            if (candidate != detected) yield return candidate;
    }

    private async Task<BalanceResult> FetchModeAsync(string root, string apiKey, BalanceQueryMode mode, CancellationToken token)
    {
        var path = mode switch
        {
            BalanceQueryMode.Usage => "/v1/usage",
            BalanceQueryMode.UserBalance => "/user/balance",
            _ => "/v1/dashboard/billing/subscription",
        };
        using var document = await ReadAsync(root + path, apiKey, token).ConfigureAwait(false);
        var body = document.RootElement;
        if (IsInvalid(body)) return new(null, string.Empty, false, true, null, null);
        if (body.ValueKind != JsonValueKind.Object) throw InvalidResponse();
        var data = ObjectProperty(body, "data");
        if (mode == BalanceQueryMode.OpenAiBilling)
        {
            if (Amount(Property(body, "hard_limit_usd")) is not { } limit) throw InvalidResponse();
            if (limit >= 100000000m) return new(null, SafeDefaultUnit("USD", apiKey), true, false, null, null);
            using var usage = await ReadAsync(root + "/v1/dashboard/billing/usage", apiKey, token).ConfigureAwait(false);
            var usageBody = usage.RootElement;
            if (IsInvalid(usageBody)) return new(null, string.Empty, false, true, null, null);
            if (Amount(Property(usageBody, "total_usage")) is not { } used) throw InvalidResponse();
            try { return new(checked(limit - used / 100m), SafeDefaultUnit("USD", apiKey), false, false, null, null); }
            catch (OverflowException) { throw new BalanceFailure("余额数值超出支持范围"); }
        }

        decimal? amount;
        string unit;
        if (mode == BalanceQueryMode.Usage)
        {
            var quota = ObjectProperty(body, "quota");
            amount = Amount(Property(body, "remaining")) ?? Amount(Property(quota, "remaining")) ?? Amount(Property(body, "balance"));
            unit = Unit(Property(body, "unit"), apiKey) ?? Unit(Property(quota, "unit"), apiKey) ?? SafeDefaultUnit("USD", apiKey);
        }
        else
        {
            var value = data.ValueKind == JsonValueKind.Object ? data : body;
            amount = Amount(Property(value, "remaining")) ?? Amount(Property(value, "balance"));
            unit = Unit(Property(value, "unit"), apiKey) ?? Unit(Property(value, "currency"), apiKey) ?? SafeDefaultUnit("CNY", apiKey);
        }
        if (amount is null) throw InvalidResponse();
        return new(amount, unit, false, false, null, null);
    }

    private async Task<JsonDocument> ReadAsync(string url, string apiKey, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("RetryProxy", _version));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var code = (int)response.StatusCode;
            // 认证失败、限流和重定向都立即停止识别，不读取可能回显密钥的错误正文。
            throw new BalanceFailure($"余额查询失败：服务商返回 HTTP {code}",
                code is 401 or 403 or 429 || code is >= 300 and < 400);
        }
        if (response.Content.Headers.ContentLength > MaxResponseBytes) throw ResponseTooLarge();
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > MaxResponseBytes) throw ResponseTooLarge();
            buffer.Write(chunk, 0, count);
        }
        try { return JsonDocument.Parse(buffer.ToArray()); }
        catch (JsonException) { throw new BalanceFailure("余额查询结果格式无效"); }
    }

    private static JsonElement Property(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) ? value : default;

    private static JsonElement ObjectProperty(JsonElement parent, string name)
    {
        var value = Property(parent, name);
        return value.ValueKind == JsonValueKind.Object ? value : default;
    }

    private static bool IsInvalid(JsonElement body) => HasFalseActiveFlag(body) || HasFalseActiveFlag(ObjectProperty(body, "data"));
    private static bool HasFalseActiveFlag(JsonElement body) => Property(body, "is_active").ValueKind == JsonValueKind.False
        || Property(body, "isActive").ValueKind == JsonValueKind.False;

    private static decimal? Amount(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return number;
        return null;
    }

    private static string? Unit(JsonElement value, string apiKey)
    {
        if (value.ValueKind != JsonValueKind.String) return null;
        var raw = value.GetString()!;
        if (raw.Length > 16 || raw.Any(char.IsControl)) return null;
        var unit = raw.Trim();
        if (unit.Length == 0 || unit.Contains(apiKey, StringComparison.Ordinal)
            || !unit.All(c => char.IsLetterOrDigit(c) || c is ' ' or '_' or '/' or '.' or '$' or '€' or '£' or '¥' or '₩' or '₽')) return null;
        return unit;
    }

    private static string SafeDefaultUnit(string unit, string apiKey) => unit.Contains(apiKey, StringComparison.Ordinal) ? string.Empty : unit;
    private static BalanceResult Failure(string message) => new(null, string.Empty, false, false, null, message);
    private static BalanceFailure InvalidResponse() => new("余额查询结果缺少有效余额字段");
    private static BalanceFailure ResponseTooLarge() => new("余额查询结果超过 1 MiB，已停止读取", terminal: true);
    private sealed class BalanceFailure(string message, bool terminal = false) : Exception(message)
    {
        public bool Terminal { get; } = terminal;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsClient) _client.Dispose();
    }
}
