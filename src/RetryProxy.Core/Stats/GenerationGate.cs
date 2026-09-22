using System;
using System.Text.Json;
using RetryProxy.Core.Internal;

namespace RetryProxy.Core.Stats;

/// <summary>
/// 生成门（对应 response_stats.rs 的 GenerationGate）：
/// 在看到第一条“非等待”事件之前，上游内容只暂存不转发，用于让等待生成超时仍可重试。
/// </summary>
internal sealed class GenerationGate
{
    private readonly EventDecoder _events = new();
    private bool _ready;

    /// <summary>观察一块上游数据；返回 true 表示已可以开始向客户端转发。</summary>
    public bool Observe(ReadOnlySpan<byte> chunk)
    {
        if (!_ready)
        {
            var any = false;
            foreach (var decoded in _events.Push(chunk))
            {
                if (!IsWaitingEvent(decoded))
                {
                    any = true;
                    break;
                }
            }

            _ready = any || _events.Unsupported || !_events.PendingLineIsSupported();
        }

        return _ready;
    }

    public bool Finish() => Observe("\n\n"u8);

    private static bool EmptyText(JsonElement? value)
    {
        return value is null || value.Value.ValueKind == JsonValueKind.Null || value.AsString() == "";
    }

    private static bool EmptyArray(JsonElement? value)
    {
        return value is null || value.Value.ValueKind == JsonValueKind.Null || value.Value.IsEmptyArray();
    }

    private static bool IsNoneOrNull(JsonElement? value) => value.IsNullOrMissing();

    internal static bool IsWaitingEvent(DecodedEvent decoded)
    {
        var data = TrimAscii(decoded.Data);
        if (data.IsEmpty)
        {
            return decoded.Name is "" or "message" or "ping" or "keepalive" or "heartbeat";
        }

        using var document = JsonText.TryParse(data);
        if (document is null)
        {
            return false;
        }

        var value = document.RootElement;
        var eventType = value.Get("type").AsString() ?? decoded.Name;
        var error = value.Get("error");
        if ((decoded.Name.Length > 0 && decoded.Name != "message" && decoded.Name != eventType)
            || ContentRules.HasGeneratedContent(value, eventType)
            || (error is not null && error.Value.ValueKind != JsonValueKind.Null))
        {
            return false;
        }

        switch (eventType)
        {
            case "ping":
            case "keepalive":
            case "heartbeat":
            {
                if (value.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                foreach (var field in value.EnumerateObject())
                {
                    switch (field.Name)
                    {
                        case "type":
                        case "timestamp":
                        case "time":
                        case "sequence_number":
                            break;
                        case "data":
                            if (!(field.Value.ValueKind == JsonValueKind.Null || field.Value.IsEmptyObject()))
                            {
                                return false;
                            }

                            break;
                        default:
                            return false;
                    }
                }

                return true;
            }

            case "response.created":
            case "response.in_progress":
            case "response.queued":
            {
                var response = value.Get("response");
                if (!response.IsObject())
                {
                    return false;
                }

                var status = response.Get("status");
                return EmptyArray(response.Get("output"))
                    && IsNoneOrNull(response.Get("error"))
                    && IsNoneOrNull(response.Get("incomplete_details"))
                    && (status is null || status.AsString() is "queued" or "in_progress");
            }

            case "response.output_item.added":
            {
                var item = value.Get("item");
                if (item is null)
                {
                    return false;
                }

                var status = item.Get("status");
                return item.Get("type").AsString() is "message" or "reasoning"
                    && EmptyArray(item.Get("content"))
                    && EmptyArray(item.Get("summary"))
                    && EmptyText(item.Get("encrypted_content"))
                    && (status is null || status.AsString() == "in_progress");
            }

            case "response.content_part.added":
            {
                var part = value.Get("part");
                return part is not null
                    && part.Get("type").AsString() == "output_text"
                    && EmptyText(part.Get("text"))
                    && EmptyArray(part.Get("annotations"))
                    && EmptyArray(part.Get("logprobs"));
            }

            case "response.reasoning_summary_part.added":
            {
                var part = value.Get("part");
                return part is not null
                    && part.Get("type").AsString() == "summary_text"
                    && EmptyText(part.Get("text"));
            }

            case "response.output_text.delta":
            case "response.refusal.delta":
            case "response.reasoning_text.delta":
            case "response.reasoning_summary_text.delta":
                return value.Get("delta").AsString() == "" && EmptyArray(value.Get("logprobs"));

            case "message_start":
            {
                var message = value.Get("message");
                return message.IsObject()
                    && EmptyArray(message.Get("content"))
                    && IsNoneOrNull(message.Get("stop_reason"))
                    && IsNoneOrNull(message.Get("error"));
            }

            case "content_block_start":
            {
                var block = value.Get("content_block");
                return block is not null
                    && block.Get("type").AsString() is "text" or "thinking"
                    && EmptyText(block.Get("text"))
                    && EmptyText(block.Get("thinking"))
                    && EmptyText(block.Get("signature"))
                    && EmptyArray(block.Get("citations"));
            }

            case "content_block_delta":
            {
                var delta = value.Get("delta");
                if (delta is null)
                {
                    return false;
                }

                return delta.Get("type").AsString() switch
                {
                    "text_delta" => delta.Get("text").AsString() == "",
                    "thinking_delta" => delta.Get("thinking").AsString() == "",
                    _ => false,
                };
            }

            case "":
            case "message":
            {
                var choices = value.Get("choices");
                if (!choices.IsArray())
                {
                    return false;
                }

                foreach (var choice in choices!.Value.EnumerateArray())
                {
                    if (!IsNoneOrNull(choice.Get("finish_reason")))
                    {
                        return false;
                    }

                    var delta = choice.Get("delta");
                    if (!delta.IsObject())
                    {
                        return false;
                    }

                    foreach (var field in delta!.Value.EnumerateObject())
                    {
                        var ok = field.Name switch
                        {
                            "role" => field.Value.AsString() == "assistant",
                            "content" or "reasoning" or "reasoning_content" => EmptyText(field.Value),
                            _ => false,
                        };
                        if (!ok)
                        {
                            return false;
                        }
                    }
                }

                return true;
            }

            default:
                return false;
        }
    }

    internal static ReadOnlySpan<byte> TrimAscii(ReadOnlySpan<byte> bytes)
    {
        var start = 0;
        var end = bytes.Length;
        while (start < end && IsAsciiWhitespace(bytes[start]))
        {
            start++;
        }

        while (end > start && IsAsciiWhitespace(bytes[end - 1]))
        {
            end--;
        }

        return bytes[start..end];
    }

    internal static bool IsAsciiWhitespace(byte value) => value is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or 0x0c;
}
