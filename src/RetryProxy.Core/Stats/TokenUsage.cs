using System.Text.Json;
using RetryProxy.Core.Internal;

namespace RetryProxy.Core.Stats;

/// <summary>token 用量累计（对应 response_stats.rs 的 TokenUsage）。</summary>
internal sealed class TokenUsage
{
    private static readonly string[] InputPointers = { "/input_tokens", "/prompt_tokens" };
    private static readonly string[] OutputPointers = { "/output_tokens", "/completion_tokens" };
    private static readonly string[] CacheReadPointers =
    {
        "/cache_read_input_tokens", "/input_tokens_details/cached_tokens", "/prompt_tokens_details/cached_tokens", "/prompt_cache_hit_tokens",
    };
    private static readonly string[] CacheCreationPointers =
    {
        "/cache_creation_input_tokens", "/input_tokens_details/cache_write_tokens", "/prompt_tokens_details/cache_write_tokens",
    };
    private static readonly string[] ReasoningPointers =
    {
        "/output_tokens_details/reasoning_tokens", "/completion_tokens_details/reasoning_tokens",
    };

    public ulong? Input { get; private set; }

    public ulong? Output { get; private set; }

    public ulong? CacheRead { get; private set; }

    public ulong? CacheCreation { get; private set; }

    public ulong? Reasoning { get; private set; }

    private bool _inputFromDelta;

    public void Update(JsonElement usage, bool messageDelta)
    {
        var input = TokenCount(usage, InputPointers);
        var output = TokenCount(usage, OutputPointers);
        var cacheRead = TokenCount(usage, CacheReadPointers);
        var cacheCreation = TokenCount(usage, CacheCreationPointers);
        var current = Input ?? 0;
        var replaceInput = input is { } next
            && (!messageDelta
                || (next > 0 && (current == 0 || next < current || (_inputFromDelta && Input == next))));
        if (replaceInput)
        {
            Input = input;
            _inputFromDelta = messageDelta;
        }
        else if (Input is null)
        {
            Input = input;
        }

        if (output is not null)
        {
            Output = output;
        }

        if (cacheRead is not null && (!messageDelta || replaceInput || (CacheRead ?? 0) == 0))
        {
            CacheRead = cacheRead;
        }

        if (cacheCreation is not null && (!messageDelta || replaceInput || (CacheCreation ?? 0) == 0))
        {
            CacheCreation = cacheCreation;
        }

        if (TokenCount(usage, ReasoningPointers) is { } reasoning)
        {
            Reasoning = reasoning;
        }
    }

    private static ulong? TokenCount(JsonElement value, string[] pointers)
    {
        foreach (var pointer in pointers)
        {
            if (value.Pointer(pointer).AsUInt64() is { } count)
            {
                return count;
            }
        }

        return null;
    }
}
