using System.Text.Json;
using RetryProxy.Core.Internal;

namespace RetryProxy.Core.Stats;

/// <summary>判断事件里是否已含生成内容（对应 response_stats.rs 的 has_generated_content）。</summary>
internal static class ContentRules
{
    public static bool NonEmpty(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()!.Length > 0,
            JsonValueKind.Array => value.GetArrayLength() > 0,
            JsonValueKind.Object => !value.IsEmptyObject(),
            _ => false,
        };
    }

    private static bool NonEmpty(JsonElement? value) => value is { } element && NonEmpty(element);

    public static bool HasGeneratedContent(JsonElement value, string eventType)
    {
        if (eventType.StartsWith("response.", System.StringComparison.Ordinal) && eventType.EndsWith(".delta", System.StringComparison.Ordinal))
        {
            return NonEmpty(value.Get("delta"));
        }

        if (eventType == "content_block_delta")
        {
            return NonEmpty(value.Pointer("/delta/text"))
                || NonEmpty(value.Pointer("/delta/thinking"))
                || NonEmpty(value.Pointer("/delta/partial_json"));
        }

        if (eventType == "content_block_start")
        {
            var kind = value.Pointer("/content_block/type").AsString();
            return (kind is not null && kind is not ("text" or "thinking"))
                || NonEmpty(value.Pointer("/content_block/text"))
                || NonEmpty(value.Pointer("/content_block/thinking"))
                || NonEmpty(value.Pointer("/content_block/signature"));
        }

        if (eventType is "response.output_item.added" or "response.output_item.done")
        {
            var kind = value.Pointer("/item/type").AsString();
            return (kind is not null && kind is not ("message" or "reasoning"))
                || NonEmpty(value.Pointer("/item/content"))
                || NonEmpty(value.Pointer("/item/summary"))
                || NonEmpty(value.Pointer("/item/encrypted_content"));
        }

        if (eventType is "response.content_part.added" or "response.content_part.done")
        {
            return NonEmpty(value.Pointer("/part/text")) || NonEmpty(value.Pointer("/part/refusal"));
        }

        var choices = value.Get("choices");
        if (choices.IsArray())
        {
            foreach (var choice in choices!.Value.EnumerateArray())
            {
                var delta = choice.Get("delta") ?? choice.Get("message");
                if (delta is null)
                {
                    continue;
                }

                foreach (var field in new[] { "content", "reasoning_content", "reasoning", "tool_calls", "function_call" })
                {
                    if (NonEmpty(delta.Get(field)))
                    {
                        return true;
                    }
                }
            }
        }

        return NonEmpty(value.Pointer("/response/output")) || NonEmpty(value.Pointer("/message/content"));
    }
}
