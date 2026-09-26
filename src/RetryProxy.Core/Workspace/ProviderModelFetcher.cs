using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RetryProxy.Core.Config;
using RetryProxy.Core.Cli;
using RetryProxy.Core.Service;

namespace RetryProxy.Core.Workspace;

public static class ProviderModelFetcher
{
    private const int MaxResponseBytes = 1024 * 1024;
    private static readonly HttpClient Client = CreateClient(SystemProxyResolver.Shared);
    private static readonly string[] CompatibilitySuffixes =
    {
        "/api/claudecode", "/api/anthropic", "/apps/anthropic", "/api/coding",
        "/claudecode", "/anthropic", "/step_plan", "/coding", "/claude",
    };

    internal static HttpClient CreateClient(IProxyResolver proxyResolver) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        UseProxy = true,
        Proxy = new ResolverWebProxy(proxyResolver),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(90),
    });

    public static Task<IReadOnlyList<string>> FetchAsync(ProviderEndpoint provider, string apiKey, ClientType clientType, CancellationToken cancellationToken,
        ClaudeAuthMode authMode = ClaudeAuthMode.Bearer)
        => FetchAsync(provider, apiKey, clientType, Client, cancellationToken, authMode);

    internal static async Task<IReadOnlyList<string>> FetchAsync(ProviderEndpoint provider, string apiKey, ClientType clientType, HttpClient client,
        CancellationToken cancellationToken, ClaudeAuthMode authMode = ClaudeAuthMode.Bearer)
    {
        foreach (var url in UrlCandidates(provider.BaseUrl))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var requestToken = timeout.Token;
            HttpResponseMessage response;
            try
            {
                var useBearer = clientType != ClientType.Claude || authMode == ClaudeAuthMode.Bearer;
                response = await SendRequestAsync(client, url, apiKey, useBearer, requestToken).ConfigureAwait(false);
                if (clientType == ClientType.Claude && response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    && !IsGatewayForbidden(response))
                {
                    // 仅在同一个地址认证失败时切换认证格式，不跟随重定向。
                    response.Dispose();
                    response = await SendRequestAsync(client, url, apiKey, !useBearer, requestToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new WorkspaceException("获取模型超时，请检查服务商地址及网络连接");
            }
            catch (HttpRequestException)
            {
                throw new WorkspaceException("获取模型失败，请检查服务商地址及网络连接");
            }

            using (response)
            {
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
                {
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    if (IsGatewayForbidden(response))
                    {
                        try
                        {
                            await using var stream = await response.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false);
                            var sample = new byte[8192];
                            var count = await stream.ReadAsync(sample.AsMemory(), requestToken).ConfigureAwait(false);
                            if (Encoding.UTF8.GetString(sample, 0, count).Contains("http_auto_ratelimit", StringComparison.OrdinalIgnoreCase))
                            {
                                throw new WorkspaceException("获取模型失败：服务商安全网关返回 HTTP 403（触发自动限流），请检查网络出口或稍后再试");
                            }
                        }
                        catch (IOException)
                        {
                        }
                        catch (HttpRequestException)
                        {
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            throw new WorkspaceException("获取模型超时，请检查服务商地址及网络连接");
                        }

                        throw new WorkspaceException("获取模型失败：服务商网关返回 HTTP 403（网页拦截），请检查网络出口；可手动输入模型");
                    }

                    throw new WorkspaceException($"获取模型失败：服务商返回 HTTP {(int)response.StatusCode}，请检查地址及 API Key");
                }

                if (response.Content.Headers.ContentLength > MaxResponseBytes)
                {
                    throw new WorkspaceException("模型列表过大，无法读取");
                }

                try
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false);
                    using var buffer = new MemoryStream();
                    var chunk = new byte[8192];
                    int count;
                    while ((count = await stream.ReadAsync(chunk, requestToken).ConfigureAwait(false)) != 0)
                    {
                        if (buffer.Length + count > MaxResponseBytes)
                        {
                            throw new WorkspaceException("模型列表过大，无法读取");
                        }

                        buffer.Write(chunk, 0, count);
                    }

                    return ParseModels(buffer.ToArray());
                }
                catch (JsonException)
                {
                    throw new WorkspaceException("模型列表格式无效");
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new WorkspaceException("获取模型超时，请检查服务商地址及网络连接");
                }
                catch (IOException)
                {
                    throw new WorkspaceException("无法读取模型列表");
                }
            }
        }

        throw new WorkspaceException("服务商未提供模型列表接口（HTTP 404/405），请检查服务商地址");
    }

    private static bool IsGatewayForbidden(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.Forbidden
        && string.Equals(response.Content.Headers.ContentType?.MediaType, "text/html", StringComparison.OrdinalIgnoreCase);

    private static async Task<HttpResponseMessage> SendRequestAsync(HttpClient client, string url, string apiKey,
        bool useBearer, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation(useBearer ? "Authorization" : "x-api-key", useBearer ? $"Bearer {apiKey}" : apiKey);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    internal static IReadOnlyList<string> UrlCandidates(string baseUrl)
    {
        var baseAddress = baseUrl.TrimEnd('/');
        var version = baseAddress[(baseAddress.LastIndexOf('/') + 1)..];
        var versioned = version.Length > 1 && version[0] == 'v' && version[1..].All(char.IsAsciiDigit);
        var urls = new List<string> { versioned ? $"{baseAddress}/models" : $"{baseAddress}/v1/models" };
        if (versioned && version != "v1")
        {
            urls.Add($"{baseAddress}/v1/models");
        }

        foreach (var suffix in CompatibilitySuffixes)
        {
            if (!baseAddress.EndsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }

            var root = baseAddress[..^suffix.Length];
            if (root.Contains("://", StringComparison.Ordinal))
            {
                urls.Add($"{root}/v1/models");
                urls.Add($"{root}/models");
            }

            break;
        }

        return urls.Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static IReadOnlyList<string> ParseModels(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        JsonElement items;
        string name;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new WorkspaceException("模型列表格式无效");
        }

        if (root.TryGetProperty("data", out items) && items.ValueKind == JsonValueKind.Array)
        {
            name = "id";
        }
        else if (root.TryGetProperty("models", out items) && items.ValueKind == JsonValueKind.Array)
        {
            name = "slug";
        }
        else
        {
            throw new WorkspaceException("服务商未返回模型列表（data/models）");
        }

        var models = items.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var id) && id.ValueKind == JsonValueKind.String)
            .Select(item => item.GetProperty(name).GetString()!)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        if (models.Length == 0)
        {
            throw new WorkspaceException("服务商未返回可用模型");
        }

        return models;
    }
}
