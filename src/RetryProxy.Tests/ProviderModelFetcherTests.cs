using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RetryProxy.Core.Config;
using RetryProxy.Core.Workspace;
using Xunit;

namespace RetryProxy.Tests;

public class ProviderModelFetcherTests
{
    [Fact]
    public void CandidatesFollowCompatibleProviderPaths()
    {
        Assert.Equal(new[] { "https://api.test/v1/models" }, ProviderModelFetcher.UrlCandidates("https://api.test/v1"));
        Assert.Equal(new[] { "https://api.test/api/coding/paas/v4/models", "https://api.test/api/coding/paas/v4/v1/models" },
            ProviderModelFetcher.UrlCandidates("https://api.test/api/coding/paas/v4"));
        Assert.Equal(new[] { "https://api.test/api/anthropic/v1/models", "https://api.test/v1/models", "https://api.test/models" },
            ProviderModelFetcher.UrlCandidates("https://api.test/api/anthropic"));
    }

    [Fact]
    public void ParsesBothModelFormatsAndRejectsEmptyLists()
    {
        Assert.Equal(new[] { "a", "z" }, ProviderModelFetcher.ParseModels(Encoding.UTF8.GetBytes("{\"data\":[{\"id\":\"z\"},{\"id\":\"a\"},{\"id\":\"z\"}]}")));
        Assert.Equal(new[] { "claude" }, ProviderModelFetcher.ParseModels(Encoding.UTF8.GetBytes("{\"models\":[{\"slug\":\"claude\"}]}")));
        Assert.Throws<WorkspaceException>(() => ProviderModelFetcher.ParseModels(Encoding.UTF8.GetBytes("{\"data\":[]}")));
    }

    [Fact]
    public async Task FetchesWithClientSpecificHeadersAndFallbackOnlyOnMissingEndpoint()
    {
        var paths = new List<string>();
        using var client = new HttpClient(new StubHandler(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            Assert.Equal("secret", request.Headers.GetValues("x-api-key").Single());
            Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single());
            return request.RequestUri.AbsolutePath == "/models"
                ? JsonResponse("{\"data\":[{\"id\":\"claude-chosen\"}]}")
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var result = await ProviderModelFetcher.FetchAsync(
            new ProviderEndpoint("claude", "https://api.test/api/anthropic"), "secret", ClientType.Claude, client, CancellationToken.None);
        Assert.Equal(new[] { "/api/anthropic/v1/models", "/v1/models", "/models" }, paths);
        Assert.Equal(new[] { "claude-chosen" }, result);

        using var codexClient = new HttpClient(new StubHandler(request =>
        {
            Assert.Equal("Bearer secret", request.Headers.GetValues("Authorization").Single());
            Assert.False(request.Headers.Contains("x-api-key"));
            return JsonResponse("{\"models\":[{\"slug\":\"codex-chosen\"}]}");
        }));
        result = await ProviderModelFetcher.FetchAsync(
            new ProviderEndpoint("codex", "https://api.test/v1"), "secret", ClientType.Codex, codexClient, CancellationToken.None);
        Assert.Equal(new[] { "codex-chosen" }, result);
    }

    [Fact]
    public async Task ErrorsDoNotDiscloseProviderResponseOrSecret()
    {
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("secret private upstream error"),
        }));
        var error = await Assert.ThrowsAsync<WorkspaceException>(() => ProviderModelFetcher.FetchAsync(
            new ProviderEndpoint("codex", "https://api.test"), "secret", ClientType.Codex, client, CancellationToken.None));
        Assert.Contains("401", error.Message);
        Assert.DoesNotContain("secret", error.Message);
        Assert.DoesNotContain("private", error.Message);
    }

    private static HttpResponseMessage JsonResponse(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
