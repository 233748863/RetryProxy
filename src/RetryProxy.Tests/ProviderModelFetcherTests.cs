using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RetryProxy.Core.Cli;
using RetryProxy.Core.Config;
using RetryProxy.Core.Service;
using RetryProxy.Core.Workspace;
using RetryProxy.Tests.Support;
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
            Assert.Equal("Bearer secret", request.Headers.GetValues("Authorization").Single());
            Assert.False(request.Headers.Contains("x-api-key"));
            Assert.False(request.Headers.Contains("anthropic-version"));
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
    public async Task FetchUsesSuppliedProxyResolverForSuppliedProviderUrl()
    {
        await using var upstream = await FakeUpstream.StartAsync(async context =>
        {
            Assert.Equal("/v1/models", context.Request.Path);
            Assert.Equal("Bearer secret", context.Request.Headers.Authorization.ToString());
            await Upstream.Text(context, 200, "{\"data\":[{\"id\":\"model-from-proxy\"}]}");
        });
        var resolver = new FixedProxyResolver(new Uri(upstream.BaseUrl));
        using var client = ProviderModelFetcher.CreateClient(resolver);

        var models = await ProviderModelFetcher.FetchAsync(
            new ProviderEndpoint("manual", "http://model-provider.test"), "secret", ClientType.Codex, client, CancellationToken.None);

        Assert.Equal(new[] { "model-from-proxy" }, models);
        Assert.True(resolver.ResolveCalls > 0);
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

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ClaudeRetriesTheSameModelEndpointWithApiKeyWhenBearerAuthIsRejected(HttpStatusCode status)
    {
        var requests = new List<string>();
        using var client = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            Assert.False(request.Headers.Contains("anthropic-version"));
            if (requests.Count == 1)
            {
                Assert.Equal("Bearer secret", request.Headers.GetValues("Authorization").Single());
                Assert.False(request.Headers.Contains("x-api-key"));
                return new HttpResponseMessage(status);
            }
            Assert.Equal("secret", request.Headers.GetValues("x-api-key").Single());
            Assert.False(request.Headers.Contains("Authorization"));
            return JsonResponse("{\"data\":[{\"id\":\"claude-opus-5-5\"}]}");
        }));

        var models = await ProviderModelFetcher.FetchAsync(
            new ProviderEndpoint("gateway", "https://api.test/api/anthropic"), "secret", ClientType.Claude, client, CancellationToken.None);

        Assert.Equal(new[] { "claude-opus-5-5" }, models);
        Assert.Equal(new[] { "https://api.test/api/anthropic/v1/models", "https://api.test/api/anthropic/v1/models" }, requests);
    }

    [Fact]
    public async Task ClaudeUsesConfiguredApiKeyHeaderBeforeFallback()
    {
        var attempts = 0;
        using var client = new HttpClient(new StubHandler(request =>
        {
            attempts++;
            Assert.Equal("secret", request.Headers.GetValues("x-api-key").Single());
            Assert.False(request.Headers.Contains("Authorization"));
            return JsonResponse("{\"data\":[{\"id\":\"claude-sonnet\"}]}");
        }));

        var models = await ProviderModelFetcher.FetchAsync(new ProviderEndpoint("claude", "https://api.test"),
            "secret", ClientType.Claude, client, CancellationToken.None, ClaudeAuthMode.ApiKey);
        Assert.Equal(new[] { "claude-sonnet" }, models);
        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData(ClientType.Claude)]
    [InlineData(ClientType.Codex)]
    public async Task HtmlGatewayForbiddenDoesNotRetryOrChangeAuthentication(ClientType clientType)
    {
        var attempts = 0;
        using var client = new HttpClient(new StubHandler(request =>
        {
            attempts++;
            Assert.Equal("/v1/models", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer secret", request.Headers.GetValues("Authorization").Single());
            Assert.False(request.Headers.Contains("x-api-key"));
            return new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("<html>blocked</html>", Encoding.UTF8, "text/html"),
            };
        }));

        var error = await Assert.ThrowsAsync<WorkspaceException>(() => ProviderModelFetcher.FetchAsync(
            new ProviderEndpoint("provider", "https://api.test"), "secret", clientType, client, CancellationToken.None));
        Assert.Contains("网关", error.Message);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task PersistentHtmlGatewayForbiddenReportsBlockWithoutExposingResponse()
    {
        var attempts = 0;
        using var client = new HttpClient(new StubHandler(request =>
        {
            attempts++;
            Assert.Equal("Bearer secret", request.Headers.GetValues("Authorization").Single());
            Assert.False(request.Headers.Contains("x-api-key"));
            return new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("<html>Denied by http_auto_ratelimit secret upstream detail</html>", Encoding.UTF8, "text/html"),
            };
        }));

        var error = await Assert.ThrowsAsync<WorkspaceException>(() => ProviderModelFetcher.FetchAsync(
            new ProviderEndpoint("claude", "https://api.test"), "secret", ClientType.Claude, client, CancellationToken.None));
        Assert.Equal(1, attempts);
        Assert.Contains("自动限流", error.Message);
        Assert.Contains("403", error.Message);
        Assert.DoesNotContain("secret", error.Message);
        Assert.DoesNotContain("upstream", error.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, 1)]
    [InlineData(HttpStatusCode.TooManyRequests, 1)]
    [InlineData(HttpStatusCode.InternalServerError, 1)]
    [InlineData(HttpStatusCode.Unauthorized, 2)]
    public async Task FailedAuthenticationDoesNotLoopOrTryUnrelatedPaths(HttpStatusCode status, int expectedRequests)
    {
        var requests = 0;
        using var client = new HttpClient(new StubHandler(request =>
        {
            requests++;
            Assert.Equal("/api/anthropic/v1/models", request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(status) { Content = new StringContent("secret upstream details") };
        }));
        var error = await Assert.ThrowsAsync<WorkspaceException>(() => ProviderModelFetcher.FetchAsync(
            new ProviderEndpoint("gateway", "https://api.test/api/anthropic"), "secret", ClientType.Claude, client, CancellationToken.None));
        Assert.Equal(expectedRequests, requests);
        Assert.Contains(((int)status).ToString(), error.Message);
        Assert.DoesNotContain("secret", error.Message);
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

    private sealed class FixedProxyResolver(Uri proxyUrl) : IProxyResolver
    {
        public int ResolveCalls { get; private set; }

        public ProxyDecision Resolve(Uri target)
        {
            ResolveCalls++;
            return new ProxyDecision(proxyUrl, true);
        }
    }
}
