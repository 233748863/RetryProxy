using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RetryProxy.Core.Metrics;

/// <summary>
/// 当日统计的 jsonl 日志（对应 daily.rs 的 StorageSpec + DailyJournal）。
/// 目录为 <c>{日志目录}/daily-statistics/{sha256(通道 ID)}/</c>，每天一个 <c>yyyy-MM-dd.jsonl</c>：
/// 首行是头（版本、日期、通道 ID/名称、是否已导入旧日志），其后每行覆盖一条请求的记录。
/// </summary>
public sealed class DailyStorage : IDailyStorage
{
    internal const uint LogVersion = 1;

    private readonly string _logDirectory;
    private readonly string _routeId;
    private readonly string _routeName;

    public DailyStorage(string logDirectory, string routeId, string routeName)
    {
        _logDirectory = logDirectory;
        _routeId = routeId;
        _routeName = routeName;
    }

    /// <summary>通道 ID 是配置数据，不能当作可信的路径片段，所以只用它的哈希做目录名。</summary>
    public string Directory
    {
        get
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(_routeId));
            return Path.Combine(_logDirectory, "daily-statistics", Convert.ToHexString(digest).ToLowerInvariant());
        }
    }

    public string JournalPath(DateOnly date) => Path.Combine(Directory, $"{DateText(date)}.jsonl");

    internal static string DateText(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public DailyJournalOpenResult Open(DateOnly date, bool importLegacy)
    {
        var directory = Directory;
        System.IO.Directory.CreateDirectory(directory);
        var path = JournalPath(date);
        if (!File.Exists(path))
        {
            // 文本日志只是一次性迁移来源：之后的每一天都有自己的日志；
            // 午夜前后完成文本可能先于统计更新写出，所以只有首日才导入。
            var firstDay = true;
            foreach (var existing in System.IO.Directory.EnumerateFiles(directory, "*.jsonl"))
            {
                _ = existing;
                firstDay = false;
                break;
            }

            var records = importLegacy && firstDay
                ? LegacyLogRestore.Restore(_logDirectory, _routeName, date)
                : new SortedDictionary<string, DailyRequest>(StringComparer.Ordinal);
            var temporary = Path.Combine(directory, $"{Path.GetRandomFileName()}.tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1))
                {
                    JournalLine.WriteHeader(stream, date, _routeId, _routeName, legacyImported: records.Count > 0);
                    foreach (var record in records.Values)
                    {
                        JournalLine.WriteRequest(stream, record);
                    }

                    stream.Flush(flushToDisk: true);
                }

                try
                {
                    File.Move(temporary, path, overwrite: false);
                }
                catch (IOException) when (File.Exists(path))
                {
                }
            }
            finally
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        return DailyJournal.Open(path, date, _routeId);
    }
}

/// <summary>jsonl 中的一行：<c>{"record":"header",...}</c> 或 <c>{"record":"request","value":{...}}</c>。</summary>
internal sealed class JournalLine
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [JsonPropertyName("record")]
    public string? Record { get; set; }

    [JsonPropertyName("version")]
    public uint? Version { get; set; }

    [JsonPropertyName("date")]
    public string? Date { get; set; }

    [JsonPropertyName("route_id")]
    public string? RouteId { get; set; }

    [JsonPropertyName("route_name")]
    public string? RouteName { get; set; }

    [JsonPropertyName("legacy_imported")]
    public bool? LegacyImported { get; set; }

    [JsonPropertyName("value")]
    public DailyRequest? Value { get; set; }

    private sealed class Header
    {
        [JsonPropertyName("record")]
        public string Record => "header";

        [JsonPropertyName("version")]
        public uint Version { get; init; }

        [JsonPropertyName("date")]
        public string Date { get; init; } = string.Empty;

        [JsonPropertyName("route_id")]
        public string RouteId { get; init; } = string.Empty;

        [JsonPropertyName("route_name")]
        public string RouteName { get; init; } = string.Empty;

        [JsonPropertyName("legacy_imported")]
        public bool LegacyImported { get; init; }
    }

    private sealed class Request
    {
        [JsonPropertyName("record")]
        public string Record => "request";

        [JsonPropertyName("value")]
        public DailyRequest Value { get; init; } = new();
    }

    public static void WriteHeader(Stream stream, DateOnly date, string routeId, string routeName, bool legacyImported)
    {
        WriteLine(stream, JsonSerializer.SerializeToUtf8Bytes(new Header
        {
            Version = DailyStorage.LogVersion,
            Date = DailyStorage.DateText(date),
            RouteId = routeId,
            RouteName = routeName,
            LegacyImported = legacyImported,
        }, Options));
    }

    public static byte[] RequestBytes(DailyRequest record) => JsonSerializer.SerializeToUtf8Bytes(new Request { Value = record }, Options);

    public static void WriteRequest(Stream stream, DailyRequest record) => WriteLine(stream, RequestBytes(record));

    private static void WriteLine(Stream stream, byte[] json)
    {
        stream.Write(json);
        stream.WriteByte((byte)'\n');
        stream.Flush();
    }

    public static JournalLine? TryParse(ReadOnlySpan<byte> line)
    {
        try
        {
            return JsonSerializer.Deserialize<JournalLine>(line, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>某一天的 jsonl 追加句柄。</summary>
internal sealed class DailyJournal : IDailyJournal, IDisposable
{
    private FileStream _file;
    private readonly string _path;
    private long? _repairOffset;

    private DailyJournal(FileStream file, string path)
    {
        _file = file;
        _path = path;
    }

    public static DailyJournalOpenResult Open(string path, DateOnly date, string routeId)
    {
        var file = OpenHandle(path, FileAccess.ReadWrite);
        try
        {
            return Restore(file, path, date, routeId);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private static FileStream OpenHandle(string path, FileAccess access)
    {
        return new FileStream(path, FileMode.Open, access, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
    }

    private static DailyJournalOpenResult Restore(FileStream file, string path, DateOnly date, string routeId)
    {
        var bytes = new byte[file.Length];
        file.Seek(0, SeekOrigin.Begin);
        file.ReadExactly(bytes);
        var headerEnd = Array.IndexOf(bytes, (byte)'\n');
        var headerLength = headerEnd < 0 ? bytes.Length : headerEnd + 1;
        var header = JournalLine.TryParse(new ReadOnlySpan<byte>(bytes, 0, headerLength));
        if (header is null
            || header.Record != "header"
            || header.Version != DailyStorage.LogVersion
            || header.Date != DailyStorage.DateText(date)
            || header.RouteId != routeId)
        {
            throw new InvalidDataException("统计日志头不匹配");
        }

        var legacy = header.LegacyImported ?? false;
        var records = new Dictionary<string, DailyRequest>(StringComparer.Ordinal);
        long offset = headerLength;
        long? truncateAt = null;
        var needsNewline = headerEnd < 0;
        ulong damaged = 0;
        var start = headerLength;
        while (start < bytes.Length)
        {
            var newline = Array.IndexOf(bytes, (byte)'\n', start);
            var count = newline < 0 ? bytes.Length - start : newline - start + 1;
            var line = new ReadOnlySpan<byte>(bytes, start, count);
            needsNewline = newline < 0;
            var parsed = JournalLine.TryParse(line);
            if (parsed is { Record: "request", Value: { } value }
                && !string.IsNullOrEmpty(value.RequestId)
                && !value.RequestId.StartsWith(ProxyMetrics.KeepAlivePrefix, StringComparison.Ordinal))
            {
                records[value.RequestId] = value;
            }
            else
            {
                damaged++;
                if (needsNewline)
                {
                    truncateAt = offset;
                    needsNewline = false;
                }
            }

            offset += count;
            start += count;
        }

        if (truncateAt is { } length)
        {
            TruncateFile(path, length);
        }

        if (needsNewline)
        {
            file.Seek(0, SeekOrigin.End);
            file.WriteByte((byte)'\n');
            file.Flush();
        }

        return new DailyJournalOpenResult
        {
            Journal = new DailyJournal(file, path),
            Records = records,
            RestoredFromLegacyLogs = legacy,
            Warning = damaged > 0 ? $"统计日志中有 {damaged} 条未写完或损坏的记录，已恢复其余可读数据" : null,
        };
    }

    public void Append(DailyRequest record)
    {
        if (_repairOffset is { } pending)
        {
            TruncateFile(_path, pending);
            _repairOffset = null;
        }

        var offset = _file.Length;
        var json = JournalLine.RequestBytes(record);
        try
        {
            _file.Seek(0, SeekOrigin.End);
            _file.Write(json);
            _file.WriteByte((byte)'\n');
            _file.Flush();
        }
        catch (Exception error)
        {
            // 绝不把后续记录接在只写了一半的 JSON 对象后面。
            _repairOffset = offset;
            TruncateFile(_path, offset);
            _repairOffset = null;
            throw error as IOException ?? new IOException(error.Message, error);
        }
    }

    /// <summary>测试用：替换底层句柄以模拟写入失败/恢复。</summary>
    internal void ReplaceStreamForTest(FileStream stream)
    {
        _file.Dispose();
        _file = stream;
    }

    internal static FileStream OpenReadOnlyForTest(string path) => OpenHandle(path, FileAccess.Read);

    internal static FileStream OpenAppendForTest(string path) => OpenHandle(path, FileAccess.ReadWrite);

    private static void TruncateFile(string path, long length)
    {
        // 用独立的写句柄截断，与追加句柄互不影响。
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
        stream.SetLength(length);
    }

    public void Dispose() => _file.Dispose();
}
