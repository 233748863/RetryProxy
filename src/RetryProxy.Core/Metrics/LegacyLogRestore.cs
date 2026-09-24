using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using RetryProxy.Core.Cache;
using RetryProxy.Core.Logging;

namespace RetryProxy.Core.Metrics;

/// <summary>
/// 从保留的人类可读日志一次性迁移当日统计（对应 legacy.rs）。绝不推断缺失的 token 字段。
/// </summary>
internal static class LegacyLogRestore
{
    private static readonly string[] FailureLabels =
    {
        "响应未完成",
        "客户端在响应转发前断开",
        "通道或后台任务已取消",
        "请求总等待达到",
        "重试耗尽",
        "已达到重试上限",
    };

    private static readonly string[] Methods = { "GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS", "CONNECT", "TRACE" };

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private sealed class LogEntry
    {
        public DateTime Timestamp;
        public string Level = string.Empty;
        public string Id = string.Empty;
        public string Body = string.Empty;
    }

    private sealed class Call
    {
        public ulong? Attempt;
        public ulong? Limit;
        public string Path = string.Empty;
        public int? Status;
    }

    public static SortedDictionary<string, DailyRequest> Restore(string directory, string routeName, DateOnly date)
    {
        var prefix = $"[{routeName}][";
        var labeledPrefix = $"[{LogSource.ChannelProxy.Label()}]{prefix}";
        var datePrefix = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var entries = new List<LogEntry>();
        // 先读最旧的轮转文件；稳定排序同时保留同一秒内多次尝试的行序。
        foreach (var suffix in new[] { ".3", ".2", ".1", string.Empty })
        {
            byte[] bytes;
            try
            {
                // 日志器还开着当前文件写入，读取时必须允许写共享（对应 Rust std::fs::read 的共享方式）。
                bytes = ReadShared(Path.Combine(directory, $"retry-proxy.log{suffix}"));
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            var start = 0;
            while (start < bytes.Length)
            {
                var newline = Array.IndexOf(bytes, (byte)'\n', start);
                if (newline < 0)
                {
                    // 进程在写行时被杀会留下不完整的片段。
                    break;
                }

                var lineBytes = new ReadOnlySpan<byte>(bytes, start, newline - start + 1);
                start = newline + 1;
                string line;
                try
                {
                    line = StrictUtf8.GetString(lineBytes);
                }
                catch (DecoderFallbackException)
                {
                    continue;
                }

                if (!line.StartsWith(datePrefix, StringComparison.Ordinal) || line.Length < 21)
                {
                    continue;
                }

                var time = line[..19];
                var rest = line[20..];
                if (!DateTime.TryParseExact(time, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
                {
                    continue;
                }

                var space = rest.IndexOf(' ');
                if (space < 0)
                {
                    continue;
                }

                var level = rest[..space];
                var body = rest[(space + 1)..];
                if (level is not ("INFO" or "WARNING" or "ERROR"))
                {
                    continue;
                }

                // 新日志显式标注来源，只导入通道代理；准备及保活即便对象同名也不进入用户统计。
                var hasSource = body.StartsWith(labeledPrefix, StringComparison.Ordinal);
                if (hasSource)
                {
                    body = body[labeledPrefix.Length..];
                }
                else if (body.StartsWith(prefix, StringComparison.Ordinal))
                {
                    body = body[prefix.Length..];
                }
                else
                {
                    continue;
                }

                var close = body.IndexOf(']');
                if (close < 0)
                {
                    continue;
                }

                var id = body[..close];
                body = body[(close + 1)..];
                if (hasSource)
                {
                    if (!id.StartsWith("请求 ", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    id = id[3..];
                }
                else if (body.TrimStart().StartsWith('['))
                {
                    // 老格式只有通道和请求 ID 两个标签，避免把同名来源下的对象误当请求。
                    continue;
                }
                // 老版本的用户请求 ID 是 8 位十六进制；保活 ID 与供应商会话消息绝不能进入用户统计。
                if (id.Length is < 8 or > 32 || !id.All(Uri.IsHexDigit))
                {
                    continue;
                }

                entries.Add(new LogEntry
                {
                    Timestamp = DateTime.SpecifyKind(timestamp, DateTimeKind.Unspecified),
                    Level = level,
                    Id = id,
                    Body = body.Trim(),
                });
            }
        }

        var ordered = entries.OrderBy(entry => entry.Timestamp).ToList();
        var records = new SortedDictionary<string, DailyRequest>(StringComparer.Ordinal);
        var attempts = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var unknownRetries = new HashSet<(string Id, DateTime Timestamp, string Body)>();
        var forcedForward = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < ordered.Count; index++)
        {
            var entry = ordered[index];
            if (!TryLocalUnixMs(entry.Timestamp, out var timestamp))
            {
                continue;
            }

            if (!records.TryGetValue(entry.Id, out var record))
            {
                record = new DailyRequest { RequestId = entry.Id };
                records[entry.Id] = record;
            }

            record.UpdatedAtUnixMs = timestamp;
            record.Sequence = (ulong)index + 1;
            var call = ParseCall(entry.Body);
            if (call?.Attempt is { } numbered)
            {
                attempts[entry.Id] = numbered;
            }

            if (entry.Body.Contains("上游不接受代理补充的缓存标识", StringComparison.Ordinal))
            {
                record.CacheKey = CacheKeyState.Added;
                record.CacheFallback = true;
            }
            else if (record.CacheKey is null)
            {
                record.CacheKey = KeyState(entry.Body);
            }

            if (entry.Body.Contains("改为完整流式转发", StringComparison.Ordinal))
            {
                forcedForward.Add(entry.Id);
            }

            var retryNotice = entry.Body.Contains("可重试，", StringComparison.Ordinal) && entry.Body.Contains("秒后再次请求", StringComparison.Ordinal);
            var attemptRetry = entry.Body.Contains("将在 ", StringComparison.Ordinal) && entry.Body.Contains("秒后重试", StringComparison.Ordinal);
            if (retryNotice || attemptRetry)
            {
                ulong? attempt = call?.Attempt;
                if (attempt is null && attempts.TryGetValue(entry.Id, out var known))
                {
                    attempt = known;
                }

                if (attempt is { } value)
                {
                    record.Retry(value);
                }
                else if (unknownRetries.Add((entry.Id, entry.Timestamp, entry.Body)))
                {
                    // 轮转可能恰好从一条重试提示开始，而带编号的那行已经没了：
                    // 只计这条明确的提示，绝不凭空补出更早的尝试。
                    record.RetryCount = Saturating.Add(record.RetryCount, 1);
                }
            }

            if (record.Outcome is not null)
            {
                continue;
            }

            if (FailureLabels.Any(label => entry.Body.Contains(label, StringComparison.Ordinal)))
            {
                record.Outcome = RequestOutcome.Failure;
                continue;
            }

            if (call is null || call.Status is not { } status)
            {
                continue;
            }

            // 警告行也可能带 HTTP 200，但报告的是失败或未完成的流。
            if (entry.Level != "INFO")
            {
                continue;
            }

            if (status == 200)
            {
                record.Outcome = RequestOutcome.Success;
                record.Cache = CacheInputAccountingRules.ForPath(call.Path) is { } inputAccounting
                    ? new CacheRequest
                    {
                        RequestId = entry.Id,
                        Model = Field(entry.Body, "模型") ?? "未获取",
                        CompletedAtUnixMs = timestamp,
                        InputTokens = TokenField(entry.Body, "输入"),
                        CachedTokens = TokenField(entry.Body, "缓存命中"),
                        CacheCreationTokens = TokenField(entry.Body, "缓存写入"),
                        InputAccounting = inputAccounting,
                        CacheKeyStatus = KeyState(entry.Body)?.Label() ?? "旧日志未记录",
                    }
                    : null;
            }
            else if (!IsRetryable(status)
                     || (call.Attempt is { } attemptNumber && call.Limit is { } limit && attemptNumber >= limit)
                     || forcedForward.Contains(entry.Id))
            {
                record.Outcome = RequestOutcome.Failure;
            }
        }

        return records;
    }

    private static bool IsRetryable(int status) => status is 408 or 425 or 429 or (>= 500 and <= 599);

    private static bool TryLocalUnixMs(DateTime local, out long unixMs)
    {
        unixMs = 0;
        var zone = TimeZoneInfo.Local;
        if (zone.IsInvalidTime(local))
        {
            return false;
        }

        // 模糊时刻取较早的一个（对应 chrono 的 earliest）。
        var offset = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).Max()
            : zone.GetUtcOffset(local);
        unixMs = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), offset).ToUnixTimeMilliseconds();
        return true;
    }

    private static Call? ParseCall(string body)
    {
        ulong? attempt = null;
        ulong? limit = null;
        var call = body;
        if (body.StartsWith("第 ", StringComparison.Ordinal))
        {
            var numbered = body[2..];
            var separator = numbered.IndexOf(" 次 ", StringComparison.Ordinal);
            if (separator < 0)
            {
                return null;
            }

            var numbers = numbered[..separator];
            call = numbered[(separator + 3)..];
            var slash = numbers.IndexOf('/');
            if (slash < 0)
            {
                return null;
            }

            if (!ulong.TryParse(numbers[..slash], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedAttempt)
                || !ulong.TryParse(numbers[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedLimit))
            {
                return null;
            }

            attempt = parsedAttempt;
            limit = parsedLimit;
        }

        var arrow = call.IndexOf(" -> ", StringComparison.Ordinal);
        if (arrow < 0)
        {
            return null;
        }

        var request = call[..arrow];
        var response = call[(arrow + 4)..];
        var space = request.IndexOf(' ');
        if (space < 0)
        {
            return null;
        }

        var method = request[..space];
        var path = request[(space + 1)..];
        if (Array.IndexOf(Methods, method) < 0)
        {
            return null;
        }

        int? status = null;
        if (response.StartsWith("上游 HTTP ", StringComparison.Ordinal))
        {
            var value = response["上游 HTTP ".Length..];
            var end = value.IndexOfAny(new[] { '，', ' ', '\r', '\n' });
            if (end >= 0)
            {
                value = value[..end];
            }

            if (ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                status = parsed;
            }
        }

        return new Call { Attempt = attempt, Limit = limit, Path = path, Status = status };
    }

    private static string? Field(string body, string label)
    {
        var marker = $"，{label} ";
        var index = body.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        var rest = body[(index + marker.Length)..];
        var end = rest.IndexOf('，');
        return end < 0 ? rest : rest[..end];
    }

    private static ulong? TokenField(string body, string label)
    {
        var value = Field(body, label);
        if (value is null)
        {
            return null;
        }

        var token = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return token is not null && ulong.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static CacheKeyState? KeyState(string body)
    {
        const string marker = "，缓存标识：";
        var index = body.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        var value = body[(index + marker.Length)..];
        var end = value.IndexOf('，');
        if (end >= 0)
        {
            value = value[..end];
        }

        foreach (var state in new[] { CacheKeyState.Client, CacheKeyState.Added, CacheKeyState.MissingSession, CacheKeyState.Unsupported })
        {
            if (state.Label() == value)
            {
                return state;
            }
        }

        return null;
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
