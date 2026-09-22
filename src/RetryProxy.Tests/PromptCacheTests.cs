using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RetryProxy.Core.Cache;
using RetryProxy.Core.Internal;
using Xunit;

namespace RetryProxy.Tests;

public class PromptCacheTests
{
    private static HeaderList Headers()
    {
        var headers = new HeaderList();
        headers.Set("thread_id", "fixture-thread");
        headers.Set("authorization", "Bearer fixture-secret");
        return headers;
    }

    private static CacheRequestBody Prepare(PromptCache cache, HeaderList headers, string body)
    {
        return cache.Prepare("POST", "/v1/responses", headers, Encoding.UTF8.GetBytes(body), false);
    }

    private static string Key(CacheRequestBody request)
    {
        using var document = JsonDocument.Parse(request.Body);
        return document.RootElement.GetProperty("prompt_cache_key").GetString()!;
    }

    private static string BodyText(CacheRequestBody request) => Encoding.UTF8.GetString(request.Body.Span);

    [Fact]
    public void OnlyTheNewFieldChangesWireBytesAndHistoryGrowthKeepsTheKey()
    {
        var cache = new PromptCache("https://fixture.invalid");
        var first = " { \"model\":\"gpt-test\",\"input\":[{\"role\":\"user\",\"content\":\"\\u4e00\"}],\"number\":1.2300e+04 } \r\n";
        var a = Prepare(cache, Headers(), first);
        var amended = BodyText(a);
        var addition = $",\"prompt_cache_key\":\"{Key(a)}\"";
        Assert.Equal(first, amended.Replace(addition, ""));
        Assert.Equal(64, Key(a).Length);
        Assert.DoesNotContain("fixture-secret", Key(a));
        var b = Prepare(
            cache,
            Headers(),
            "{\"model\":\"gpt-test\",\"input\":[{\"role\":\"user\",\"content\":\"changed\"},{\"role\":\"assistant\",\"content\":\"answer\"}]}");
        Assert.Equal(Key(a), Key(b));
        Assert.Equal(
            Key(a),
            Key(Prepare(new PromptCache("https://fixture.invalid"), Headers(), first)));
    }

    [Fact]
    public void ExistingValuesAreBytePreservedIncludingAnExplicitEmptyValue()
    {
        var cache = new PromptCache("https://fixture.invalid");
        foreach (var value in new[] { "null", "\"\"", "\"client-key\"", "123", "{}" })
        {
            var body = $"{{ \"model\":\"gpt-test\", \"prompt_cache_key\":{value}, \"input\":\"question\" }}\n";
            var request = Prepare(cache, Headers(), body);
            Assert.Equal(CacheKeyState.Client, request.State);
            Assert.Equal(body, BodyText(request));
        }
    }

    [Fact]
    public void KeysAreIsolatedByAccountModelEndpointUpstreamAndConversation()
    {
        var cache = new PromptCache("https://fixture.invalid");
        var body = "{\"model\":\"gpt-test\",\"input\":\"question\"}";
        var original = Key(Prepare(cache, Headers(), body));
        foreach (var name in new[] { "authorization", "thread_id", "openai-project" })
        {
            var changed = Headers();
            changed.Set(name, "different-value");
            Assert.NotEqual(original, Key(Prepare(cache, changed, body)));
        }

        Assert.NotEqual(original, Key(Prepare(cache, Headers(), body.Replace("gpt-test", "gpt-other"))));
        Assert.NotEqual(original, Key(Prepare(new PromptCache("https://another.invalid"), Headers(), body)));
        Assert.NotEqual(
            original,
            Key(cache.Prepare("POST", "/v1/chat/completions", Headers(), Encoding.UTF8.GetBytes(body), false)));
    }

    [Fact]
    public void CodexBodyMetadataUsesTheThreadAndIgnoresChangingTurnIds()
    {
        var cache = new PromptCache("https://fixture.invalid");
        var captured = Headers();
        captured.Remove("thread_id");
        static string Request(string turn)
        {
            var turnMetadata = new JsonObject
            {
                ["thread_id"] = "fixture-thread",
                ["turn_id"] = turn,
            }.ToJsonString();
            return new JsonObject
            {
                ["model"] = "gpt-test",
                ["input"] = "question",
                ["client_metadata"] = new JsonObject
                {
                    ["x-codex-turn-metadata"] = turnMetadata,
                },
            }.ToJsonString();
        }

        var a = Prepare(cache, captured, Request("one"));
        var b = Prepare(cache, captured, Request("two"));
        Assert.Equal(Key(a), Key(b));
        Assert.Equal(
            Key(a),
            Key(Prepare(cache, Headers(), "{\"model\":\"gpt-test\",\"input\":\"question\"}")));
        var body = "{\"model\":\"gpt-test\",\"previous_response_id\":\"changes-every-turn\",\"metadata\":{\"turn_id\":\"also-changes\"}}";
        var absent = Prepare(cache, new HeaderList(), body);
        Assert.Equal(CacheKeyState.MissingSession, absent.State);
        Assert.Equal(body, BodyText(absent));
    }

    [Fact]
    public void OtherProtocolsBackgroundSignedEncodedAndOversizedBodiesStayUnchanged()
    {
        var cache = new PromptCache("https://fixture.invalid");
        var body = "{\"model\":\"gpt-test\",\"input\":\"question\"}";
        var bytes = Encoding.UTF8.GetBytes(body);
        foreach (var (method, path, background) in new[]
                 {
                     ("PUT", "/v1/responses", false),
                     ("POST", "/v1/messages", false),
                     ("POST", "/v1/responses/compact", false),
                     ("POST", "/v1/responses", true),
                 })
        {
            Assert.Equal(body, BodyText(cache.Prepare(method, path, Headers(), bytes, background)));
        }

        foreach (var (name, value) in new[]
                 {
                     ("content-encoding", "gzip"),
                     ("content-type", "text/plain"),
                     ("digest", "fixture-signature"),
                 })
        {
            var changed = Headers();
            changed.Set(name, value);
            Assert.Equal(body, BodyText(cache.Prepare("POST", "/responses", changed, bytes, false)));
        }

        var large = "{\"model\":\"gpt-test\",\"input\":\"" + new string('x', PromptCache.MaxRequestInspectionBytes) + "\"}";
        Assert.Equal(large, BodyText(Prepare(cache, Headers(), large)));
        foreach (var other in new[] { "not-json", "[]", "{\"model\":\"claude-test\"}" })
        {
            Assert.Equal(other, BodyText(Prepare(cache, Headers(), other)));
        }
    }

    [Fact]
    public void RejectionRestoresOriginalBytesAndIsScopedWithoutOverridingClientKeys()
    {
        var cache = new PromptCache("https://fixture.invalid");
        var body = "{ \"model\":\"gpt-test\", \"input\":\"question\" }";
        var request = Prepare(cache, Headers(), body);
        cache.Reject(request);
        Assert.False(request.IsAmended);
        Assert.Equal(body, BodyText(request));
        Assert.Equal(CacheKeyState.Unsupported, Prepare(cache, Headers(), body).State);
        Assert.Equal(CacheKeyState.Added, Prepare(cache, Headers(), body.Replace("gpt-test", "gpt-other")).State);
        var otherAccount = Headers();
        otherAccount.Set("authorization", "Bearer another-fixture");
        Assert.Equal(CacheKeyState.Added, Prepare(cache, otherAccount, body).State);
        var supplied = "{\"model\":\"gpt-test\",\"prompt_cache_key\":\"client\"}";
        Assert.Equal(supplied, BodyText(Prepare(cache, Headers(), supplied)));
    }

    [Fact]
    public void OnlyAnUnambiguousRejectionOfTheAddedFieldAllowsResending()
    {
        foreach (var value in new[]
                 {
                     "{\"error\":{\"param\":\"prompt_cache_key\",\"code\":\"unsupported_parameter\"}}",
                     "{\"error\":{\"message\":\"Unrecognized request argument supplied: prompt_cache_key\"}}",
                     "{\"error\":{\"message\":\"Unknown parameter: 'prompt_cache_key'.\"}}",
                     "{\"detail\":[{\"type\":\"extra_forbidden\",\"loc\":[\"body\",\"prompt_cache_key\"]}]}",
                 })
        {
            Assert.True(PromptCache.RejectsCacheKey(Encoding.UTF8.GetBytes(value)));
        }

        foreach (var value in new[]
                 {
                     "{\"error\":{\"param\":\"model\",\"code\":\"unsupported_parameter\"}}",
                     "{\"error\":{\"param\":\"prompt_cache_key\",\"code\":\"invalid_value\",\"message\":\"Key too long\"}}",
                     "{\"error\":{\"message\":\"The response mentions: Unknown parameter: 'prompt_cache_key'.\"}}",
                     "{\"error\":{\"message\":\"Unsupported parameter: 'prompt_cache_retention'.\"}}",
                     "{\"error\":{\"code\":\"invalid_responses_request\",\"message\":\"invalid codex request\"}}",
                 })
        {
            Assert.False(PromptCache.RejectsCacheKey(Encoding.UTF8.GetBytes(value)));
        }

        Assert.False(PromptCache.RejectsCacheKey(Encoding.UTF8.GetBytes("not-json")));
    }
}
