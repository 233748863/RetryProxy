using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using RetryProxy.Core.Cache;
using RetryProxy.Core.Internal;
using RetryProxy.Core.Metrics;

namespace RetryProxy.Core.Stats;

internal enum StreamOutcomeKind
{
    Complete,
    Failed,
}

internal readonly struct StreamOutcome
{
    private StreamOutcome(StreamOutcomeKind kind, string? reason)
    {
        Kind = kind;
        Reason = reason;
    }

    public StreamOutcomeKind Kind { get; }

    public string? Reason { get; }

    public bool IsFailed => Kind == StreamOutcomeKind.Failed;

    public static StreamOutcome Complete { get; } = new(StreamOutcomeKind.Complete, null);

    public static StreamOutcome Failed(string reason) => new(StreamOutcomeKind.Failed, reason);
}

internal enum BodyFormat
{
    Detect,
    Json,
    EventStream,
    Opaque,
}

/// <summary>
/// 观察上游响应正文，提取模型、用量、完成事件与错误诊断（对应 response_stats.rs 的 ResponseStats）。
/// </summary>
internal sealed class ResponseStats
{
    public const int MaxObservationBytes = 8 * 1024 * 1024;

    private BodyFormat _format;
    private string? _model;
    private readonly string? _requestModel;
    private readonly string? _requestModelIdentity;
    private string? _responseModelIdentity;
    private readonly string? _requestEffort;
    private string? _responseModel;
    private string? _responseEffort;
    private readonly TokenUsage _usage = new();
    private bool _isApiResponse;
    private readonly CacheInputAccounting? _cacheInputAccounting;
    private CacheKeyState _cacheKeyState = CacheKeyState.Unchanged;
    private double? _firstByteSeconds;
    private double? _firstContentSeconds;
    private StreamOutcome? _outcome;
    private bool _missingTerminalEvent;
    private readonly ByteBuffer _jsonBody = new();
    private bool _jsonOverflow;
    private readonly EventDecoder _events = new();
    private bool _captureAnswer;
    private readonly StringBuilder _answer = new();
    private bool _answerOverflow;
    private string? _errorDetail;
    private readonly List<(string Label, string Value)> _errorFields = new();
    private readonly string? _upstreamRequestId;
    private string? _lastEventType;
    private readonly ContentDecoder? _decoder;
    private readonly string? _contentEncoding;

    public ResponseStats(HeaderList headers, string path, string? model, string? reasoningEffort = null, string? modelIdentity = null)
    {
        var contentType = (headers.Get("content-type") ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
        var encoding = headers.Get("content-encoding");
        // 可解压的编码边收边解压后再解析；不支持的编码只转发不解析。
        _decoder = ContentDecoder.Create(encoding);
        _contentEncoding = ContentDecoder.IsEncoded(encoding) ? encoding!.Trim().ToLowerInvariant() : null;
        _format = _contentEncoding is not null && _decoder is null ? BodyFormat.Opaque
            : contentType == "text/event-stream" ? BodyFormat.EventStream
            : contentType == "application/json" || contentType.EndsWith("+json", StringComparison.Ordinal) ? BodyFormat.Json
            : BodyFormat.Detect;
        var trimmedPath = path.TrimEnd('/');
        _isApiResponse = model is not null
            || trimmedPath.EndsWith("/messages", StringComparison.Ordinal)
            || trimmedPath.EndsWith("/responses", StringComparison.Ordinal)
            || trimmedPath.EndsWith("/chat/completions", StringComparison.Ordinal);
        _model = DiagnosticText.CleanModel(model);
        _requestModel = _model;
        _requestModelIdentity = modelIdentity ?? DiagnosticText.ModelIdentity(model);
        _requestEffort = DiagnosticText.CleanDiagnosticIdentifier(reasoningEffort);
        _cacheInputAccounting = CacheInputAccountingRules.ForPath(path);
        foreach (var name in new[] { "x-request-id", "request-id", "x-oneapi-request-id" })
        {
            var cleaned = DiagnosticText.CleanDiagnosticIdentifier(headers.Get(name));
            if (cleaned is not null)
            {
                _upstreamRequestId = cleaned;
                break;
            }
        }
    }

    public ResponseStats WithAnswerCapture()
    {
        _captureAnswer = true;
        return this;
    }

    public ResponseStats WithCacheKeyState(CacheKeyState state)
    {
        _cacheKeyState = state;
        return this;
    }

    public string? Answer()
    {
        var trimmed = _answer.ToString().Trim();
        return !_answerOverflow && trimmed.Length > 0 ? trimmed : null;
    }

    public string? FailureReason()
    {
        if (_outcome is not { IsFailed: true } outcome)
        {
            return null;
        }

        if (_format == BodyFormat.Json && _errorDetail is not null)
        {
            return $"{outcome.Reason}：{_errorDetail}";
        }

        return outcome.Reason;
    }

    public ulong? ContextTokens(bool claude)
    {
        if (_usage.Input is not { } input || _usage.Output is not { } output)
        {
            return null;
        }

        try
        {
            if (claude)
            {
                input = checked(input + (_usage.CacheRead ?? 0));
                input = checked(input + (_usage.CacheCreation ?? 0));
            }

            return checked(input + output);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private (ulong Input, ulong Cached)? CacheUsage()
    {
        if (_cacheInputAccounting is not { } accounting)
        {
            return null;
        }

        var input = accounting.TotalInputTokens(_usage.Input, _usage.CacheRead, _usage.CacheCreation);
        if (input is null || _usage.CacheRead is not { } cached)
        {
            return null;
        }

        return input.Value > 0 && cached <= input.Value ? (input.Value, cached) : null;
    }

    public CacheRequest? CacheRequest(string requestId)
    {
        if (requestId.StartsWith(ProxyMetrics.KeepAlivePrefix, StringComparison.Ordinal) || _cacheInputAccounting is not { } accounting)
        {
            return null;
        }

        return new CacheRequest
        {
            RequestId = requestId,
            Model = _model ?? "未获取",
            CompletedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            InputTokens = _usage.Input,
            CachedTokens = _usage.CacheRead,
            CacheCreationTokens = _usage.CacheCreation,
            InputAccounting = accounting,
            CacheKeyStatus = _cacheKeyState.Label() ?? "保持原请求",
        };
    }

    private void AppendAnswer(string text)
    {
        if (_answerOverflow)
        {
            return;
        }

        var current = Encoding.UTF8.GetByteCount(_answer.ToString());
        if ((long)current + Encoding.UTF8.GetByteCount(text) > 512 * 1024)
        {
            _answer.Clear();
            _answerOverflow = true;
        }
        else
        {
            _answer.Append(text);
        }
    }

    private void ObserveAnswer(JsonElement value, JsonElement envelope, string eventType)
    {
        if (!_captureAnswer)
        {
            return;
        }

        var delta = eventType switch
        {
            "response.output_text.delta" => value.Get("delta").AsString(),
            "content_block_delta" => value.Pointer("/delta/text").AsString(),
            "content_block_start" => value.Pointer("/content_block/text").AsString(),
            _ => value.Pointer("/choices/0/delta/content").AsString(),
        };
        if (delta is not null)
        {
            AppendAnswer(delta);
        }

        var completed = new StringBuilder();
        var outputs = envelope.Get("output");
        if (outputs.IsArray())
        {
            foreach (var output in outputs!.Value.EnumerateArray())
            {
                var content = output.Get("content");
                if (!content.IsArray())
                {
                    continue;
                }

                foreach (var block in content!.Value.EnumerateArray())
                {
                    if (block.Get("text").AsString() is { } text)
                    {
                        completed.Append(text);
                    }
                }
            }
        }

        var envelopeContent = envelope.Get("content");
        if (envelopeContent.IsArray())
        {
            foreach (var block in envelopeContent!.Value.EnumerateArray())
            {
                if (block.Get("type").AsString() == "text" && block.Get("text").AsString() is { } text)
                {
                    completed.Append(text);
                }
            }
        }

        if (value.Pointer("/choices/0/message/content").AsString() is { } messageText)
        {
            completed.Append(messageText);
        }

        if (completed.Length > 0)
        {
            _answer.Clear();
            AppendAnswer(completed.ToString());
        }

        var stopReason = (envelope.Get("stop_reason") ?? value.Pointer("/delta/stop_reason")).AsString();
        if (stopReason == "max_tokens" || value.Pointer("/choices/0/finish_reason").AsString() == "length")
        {
            _answerOverflow = true;
        }
    }

    public void Observe(ReadOnlySpan<byte> chunk, double elapsed)
    {
        if (chunk.IsEmpty)
        {
            return;
        }

        if (_decoder is not null && _format != BodyFormat.Opaque)
        {
            // 得出结果后仍继续解压：转发层要据此判断压缩流是否已完整结束。
            var decoded = _decoder.Push(chunk);
            if (_decoder.Failed)
            {
                if (_outcome is null)
                {
                    // 解压失败后已解析的部分不可信：按无法解析的正文处理，不再据此判断完成与否。
                    _format = BodyFormat.Opaque;
                }

                return;
            }

            if (_outcome is not null)
            {
                return;
            }

            chunk = decoded;
            if (chunk.IsEmpty)
            {
                return;
            }
        }
        else if (_outcome is not null)
        {
            return;
        }

        _firstByteSeconds ??= elapsed;
        if (_format == BodyFormat.Detect)
        {
            var index = 0;
            while (index < chunk.Length && GenerationGate.IsAsciiWhitespace(chunk[index]))
            {
                index++;
            }

            if (index == chunk.Length)
            {
                return;
            }

            _format = chunk[index] switch
            {
                (byte)'{' or (byte)'[' => BodyFormat.Json,
                (byte)':' or (byte)'d' or (byte)'e' or (byte)'r' => BodyFormat.EventStream,
                _ => BodyFormat.Opaque,
            };
        }

        switch (_format)
        {
            case BodyFormat.Json when !_jsonOverflow:
                if ((long)_jsonBody.Length + chunk.Length <= MaxObservationBytes)
                {
                    _jsonBody.Append(chunk);
                }
                else
                {
                    _jsonBody.Clear();
                    _jsonOverflow = true;
                }

                break;
            case BodyFormat.EventStream:
                foreach (var decoded in _events.Push(chunk))
                {
                    ObserveEvent(decoded, elapsed);
                }

                break;
        }
    }

    public void Finish(double elapsed)
    {
        switch (_format)
        {
            case BodyFormat.Json when !_jsonOverflow:
                using (var document = JsonText.TryParse(_jsonBody.Span))
                {
                    if (document is not null)
                    {
                        ObserveValue(document.RootElement, string.Empty, elapsed);
                    }
                }

                _jsonBody.Clear();
                break;
            case BodyFormat.EventStream:
                foreach (var decoded in _events.Push("\n\n"u8))
                {
                    ObserveEvent(decoded, elapsed);
                }

                if (_outcome is null && _isApiResponse)
                {
                    _missingTerminalEvent = true;
                    _outcome = StreamOutcome.Failed("上游流提前结束，未收到完成事件");
                }

                break;
        }
    }

    public StreamOutcome? Outcome => _outcome;

    public bool MissingTerminalEvent => _missingTerminalEvent;

    public double? FirstContentSeconds()
    {
        return _format == BodyFormat.EventStream && _isApiResponse ? _firstContentSeconds : _firstByteSeconds;
    }

    public bool IsApiEventStream => _isApiResponse && _format == BodyFormat.EventStream;

    /// <summary>压缩正文已能解析但尚未收到压缩结束标记：此时切断会让客户端收到不完整的压缩流。</summary>
    public bool AwaitingEncodedEnd => _decoder is { Failed: false, Ended: false, EndDetectable: true } && _format != BodyFormat.Opaque;

    /// <summary>响应声明的内容编码（小写）；未压缩为 null。</summary>
    public string? ContentEncoding => _contentEncoding;

    public string LogFields() => FormatLogFields(_outcome is { IsFailed: true });

    public string FailureLogFields() => FormatLogFields(true);

    internal static bool IsRateLimitIdentifier(string? value) =>
        value is "rate_limit_exceeded" or "rate_limit_error" or "too_many_requests";

    public string? FailureSummary()
    {
        foreach (var (label, value) in _errorFields)
        {
            switch (label)
            {
                case "上游错误码":
                case "上游错误类型":
                    switch (value)
                    {
                        case var identifier when IsRateLimitIdentifier(identifier):
                            return "上游请求超限";
                        case "insufficient_quota":
                            return "上游可用额度不足";
                        case "overloaded_error":
                        case "server_overloaded":
                            return "上游服务繁忙";
                        case "context_length_exceeded":
                            return "请求内容超过上游长度限制";
                        case "invalid_api_key":
                        case "authentication_error":
                            return "上游身份验证失败";
                        case "server_error":
                        case "api_error":
                            return "上游服务内部错误";
                        // new-api / one-api 系中转站：当前分组没有可用渠道，多为该模型负载已满。
                        case "get_channel_failed":
                            return "当前需求量高，模型负载已达上限";
                        case "model_not_found":
                            return "上游没有这个模型";
                        case "insufficient_user_quota":
                        case "pre_consume_token_quota_failed":
                        case "quota_exceeded":
                            return "上游可用额度不足";
                        case "invalid_request_error":
                        case "invalid_request":
                            return "请求参数有误";
                        case "permission_error":
                        case "permission_denied":
                        case "access_denied":
                            return "没有访问权限";
                        case "not_found_error":
                            return "地址或模型不存在";
                        case "request_too_large":
                            return "请求内容过大";
                        case "billing_error":
                            return "上游账户计费异常";
                        case "service_unavailable":
                            return "上游服务暂不可用";
                        case "timeout_error":
                            return "上游处理超时";
                        case "bad_response_status_code":
                        case "do_request_failed":
                            return "中转站连不上它的上游渠道";
                    }

                    break;
                case "未完成原因":
                    if (value is "max_output_tokens" or "max_tokens")
                    {
                        return "达到上游输出长度限制";
                    }

                    if (value == "content_filter")
                    {
                        return "上游内容审核拦截";
                    }

                    break;
            }
        }

        return null;
    }

    private string FormatLogFields(bool failed)
    {
        var fields = new StringBuilder();
        if (failed)
        {
            foreach (var (label, value) in _errorFields)
            {
                var shortLabel = label switch
                {
                    "上游错误码" => "错误码",
                    "上游错误类型" => "错误类型",
                    "错误参数" => "参数",
                    "未完成原因" => "未完成",
                    _ => label,
                };
                fields.Append('，').Append(shortLabel).Append(' ').Append(value);
            }

            if (_upstreamRequestId is not null)
            {
                fields.Append("，上游 ID ").Append(_upstreamRequestId);
            }

            if (_format == BodyFormat.EventStream && _isApiResponse)
            {
                fields.Append("，内容 ").Append(_firstContentSeconds is not null ? "已收到" : "未收到");
                if (_lastEventType is not null)
                {
                    fields.Append("，最后事件 ").Append(_lastEventType);
                }
            }
        }

        if (_isApiResponse)
        {
            AppendComparison(fields, "模型", _requestModel, _responseModel, _requestModelIdentity != _responseModelIdentity);
            AppendComparison(fields, "思考", _requestEffort, _responseEffort);
        }
        else if (_model is not null)
        {
            fields.Append("，模型 ").Append(DiagnosticText.ComparisonDisplay(_model));
        }

        // 失败时两项用量都没读到就整段不写，只读到一项才提示不完整。
        if (_isApiResponse && !(failed && _usage.Input is null && _usage.Output is null))
        {
            fields.Append("，输入/输出 ").Append(Count(_usage.Input)).Append('/').Append(Count(_usage.Output)).Append(" token");
            if (failed && (_usage.Input is null || _usage.Output is null))
            {
                fields.Append("（用量不全）");
            }
        }

        AppendCacheFields(fields);
        if (_usage.Reasoning is { } reasoning)
        {
            fields.Append("，推理 ").Append(reasoning.ToString(CultureInfo.InvariantCulture)).Append(" token");
        }

        if (_cacheKeyState.LogLabel() is { } keyLabel)
        {
            fields.Append("，缓存标识 ").Append(keyLabel);
        }

        if (_contentEncoding is not null)
        {
            fields.Append("，压缩 ").Append(_contentEncoding);
            if (_decoder is null)
            {
                fields.Append("（未支持，未解析）");
            }
            else if (_decoder.Failed)
            {
                fields.Append("（解压失败）");
            }
        }

        return fields.ToString();
    }

    private static void AppendComparison(StringBuilder fields, string label, string? sent, string? reported, bool? mismatch = null)
    {
        // 两端都缺失时省略；明确相同时只写一次，单端缺失仍保留方向，避免把“未报告”当作已确认。
        if (sent is null && reported is null)
        {
            return;
        }

        var bothKnown = sent is not null && reported is not null;
        var different = mismatch ?? !string.Equals(sent, reported, StringComparison.Ordinal);
        fields.Append('，').Append(label).Append(' ').Append(DiagnosticText.ComparisonDisplay(sent ?? "未指定"));
        if (bothKnown && !different)
        {
            return;
        }

        fields.Append(" -> ").Append(DiagnosticText.ComparisonDisplay(reported ?? "未报告"));
        if (bothKnown && different)
        {
            fields.Append(" (不一致)");
        }
    }

    private static string Count(ulong? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "未获取";

    /// <summary>
    /// 缓存用量合成一段，例：<c>，缓存 95.9%（读 114681 / 写 4907）</c>；零命中直接写读 0。
    /// 算不出命中率时写 <c>，缓存（读 42）</c>；上游没报缓存用量时不写。
    /// </summary>
    private void AppendCacheFields(StringBuilder fields)
    {
        var usage = CacheUsage();
        var parts = new List<string>(2);
        if (usage is { Cached: 0 })
        {
            parts.Add("读 0");
        }
        else if (_usage.CacheRead is { } read)
        {
            parts.Add($"读 {read.ToString(CultureInfo.InvariantCulture)}");
        }

        if (_usage.CacheCreation is { } creation)
        {
            parts.Add($"写 {creation.ToString(CultureInfo.InvariantCulture)}");
        }

        if (parts.Count == 0)
        {
            return;
        }

        fields.Append("，缓存");
        if (usage is { } known)
        {
            fields.Append(' ').Append((100.0 * known.Cached / known.Input).ToString("F1", CultureInfo.InvariantCulture)).Append('%');
        }

        fields.Append('（').Append(string.Join(" / ", parts)).Append('）');
    }

    private void ObserveEvent(DecodedEvent decoded, double elapsed)
    {
        if (_outcome is not null)
        {
            return;
        }

        if (GenerationGate.TrimAscii(decoded.Data).SequenceEqual("[DONE]"u8))
        {
            _isApiResponse = true;
            _outcome = StreamOutcome.Complete;
            return;
        }

        using var document = JsonText.TryParse(decoded.Data.AsSpan());
        if (document is not null)
        {
            ObserveValue(document.RootElement, decoded.Name, elapsed);
        }
    }

    private void ObserveValue(JsonElement value, string eventName, double elapsed)
    {
        var eventType = value.Get("type").AsString() ?? eventName;
        if (eventType.Length > 0)
        {
            _lastEventType = DiagnosticText.CleanDiagnosticIdentifier(eventType);
        }

        var responseEnvelope = value.Get("response");
        var messageEnvelope = value.Get("message");
        var envelope = responseEnvelope.IsObject() ? responseEnvelope!.Value
            : messageEnvelope.IsObject() ? messageEnvelope!.Value
            : value;
        if (DiagnosticText.CleanModel(envelope.Get("model").AsString()) is { } model)
        {
            _model = model;
            _responseModel = model;
            _responseModelIdentity = DiagnosticText.ModelIdentity(envelope.Get("model").AsString());
            _isApiResponse = true;
        }

        if (RequestMetadata.ReadReasoningEffort(envelope) is { } effort)
        {
            _responseEffort = effort;
        }

        var usage = envelope.Get("usage");
        if (usage.IsObject())
        {
            _usage.Update(usage!.Value, eventType == "message_delta");
            _isApiResponse = true;
        }

        if (eventType.StartsWith("response.", StringComparison.Ordinal)
            || eventType.StartsWith("message_", StringComparison.Ordinal)
            || eventType.StartsWith("content_block_", StringComparison.Ordinal)
            || value.Get("choices") is not null)
        {
            _isApiResponse = true;
        }

        if (_firstContentSeconds is null && ContentRules.HasGeneratedContent(value, eventType))
        {
            _firstContentSeconds = elapsed;
        }

        ObserveAnswer(value, envelope, eventType);
        var envelopeError = envelope.Get("error");
        _outcome = eventType switch
        {
            "response.completed" or "message_stop" => StreamOutcome.Complete,
            "response.failed" => StreamOutcome.Failed("上游返回 response.failed"),
            "response.incomplete" => StreamOutcome.Failed("上游返回 response.incomplete"),
            "error" => StreamOutcome.Failed("上游返回错误事件"),
            _ when envelopeError is not null && envelopeError.Value.ValueKind != JsonValueKind.Null => StreamOutcome.Failed("上游返回错误响应"),
            _ when envelope.Get("status").AsString() == "incomplete" => StreamOutcome.Failed("上游返回未完成的响应"),
            _ => _outcome,
        };
        if (_outcome is { IsFailed: true })
        {
            ObserveFailure(value, envelope, eventType);
        }
    }

    private void ObserveFailure(JsonElement value, JsonElement envelope, string eventType)
    {
        _errorDetail = null;
        foreach (var candidate in new[]
                 {
                     envelope.Pointer("/error/message"), value.Pointer("/error/message"), envelope.Get("message"), value.Get("message"),
                 })
        {
            if (candidate.AsString() is { } text)
            {
                _errorDetail = DiagnosticText.SanitizeErrorDetail(text);
                break;
            }
        }

        var envelopeError = envelope.Get("error");
        var valueError = value.Get("error");
        var error = envelopeError.IsObject() ? envelopeError!.Value
            : valueError.IsObject() ? valueError!.Value
            : value;
        var kind = error.Get("type");
        if (kind.AsString() == eventType)
        {
            kind = null;
        }

        foreach (var (label, field) in new (string, JsonElement?)[]
                 {
                     ("上游错误码", error.Get("code")),
                     ("上游错误类型", kind),
                     ("错误参数", error.Get("param")),
                     ("未完成原因", envelope.Pointer("/incomplete_details/reason")),
                 })
        {
            if (field is not { } element)
            {
                continue;
            }

            string? identifier = element.ValueKind switch
            {
                JsonValueKind.String => DiagnosticText.CleanDiagnosticIdentifier(element.GetString()),
                JsonValueKind.Number when element.IsInteger() => element.GetRawText(),
                _ => null,
            };
            if (identifier is not null)
            {
                _errorFields.Add((label, identifier));
            }
        }
    }
}
