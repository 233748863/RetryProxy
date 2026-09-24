using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RetryProxy.Core.Config;

namespace RetryProxy.Core.Workspace;

public static class ProviderModelFetcher
{
    private const int MaxResponseBytes = 1024 * 1024;
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false });
    private static readonly string[] CompatibilitySuffixes =
    {
        "/api/claudecode", "/api/anthropic", "/apps/anthropic", "/api/coding",
        "/claudecode", "/anthropic", "/step_plan", "/coding", "/claude",
    };

    public static Task<IReadOnlyList<string>> FetchAsync(ProviderEndpoint provider, string apiKey, ClientType clientType, CancellationToken cancellationToken)
        => FetchAsync(provider, apiKey, clientType, Client, cancellationToken);

    internal static async Task<IReadOnlyList<string>> FetchAsync(ProviderEndpoint provider, string apiKey, ClientType clientType, HttpClient client, CancellationToken cancellationToken)
    {
        foreach (var url in UrlCandidates(provider.BaseUrl))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var requestToken = timeout.Token;
            HttpResponseMessage response;
            try
            {
                response = await SendRequestAsync(client, url, apiKey, clientType, clientType != ClientType.Claude, requestToken).ConfigureAwait(false);
                if (clientType == ClientType.Claude && response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    // 部分中转站的 Claude 请求接受 x-api-key，但模型目录只接受 OpenAI 兼容的 Bearer。
                    // 仅在同一个地址认证失败时更换格式；不跟随重定向，也不在日志中保留密钥或响应正文。
                    response.Dispose();
                    response = await SendRequestAsync(client, url, apiKey, clientType, useBearer: true, requestToken).ConfigureAwait(false);
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

    private static async Task<HttpResponseMessage> SendRequestAsync(HttpClient client, string url, string apiKey,
        ClientType clientType, bool useBearer, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation(useBearer ? "Authorization" : "x-api-key", useBearer ? $"Bearer {apiKey}" : apiKey);
        if (clientType == ClientType.Claude)
        {
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }
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
