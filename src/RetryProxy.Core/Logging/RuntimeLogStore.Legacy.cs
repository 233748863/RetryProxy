using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using RetryProxy.Core.Workspace;

namespace RetryProxy.Core.Logging;

public sealed partial class RuntimeLogStore
{
    private async Task ImportLegacyAsync()
    {
        // 从最旧的备份开始；一次迁移失败时按文件哈希、行序号去重，重启不会重复导入。
        for (var index = 3; index >= 0; index--)
        {
            var path = Path.Combine(LogDirectory, "retry-proxy.log" + (index == 0 ? "" : "." + index));
            if (!File.Exists(path)) continue;
            string session;
            using (var input = File.OpenRead(path)) session = "legacy-" + Convert.ToHexString(SHA256.HashData(input));
            var batch = new List<PendingEntry>(256);
            long sequence = 0;
            using (var input = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                string? current = null;
                DateTime currentTime = default;
                while (await input.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    if (TryTimestamp(line, out var timestamp))
                    {
                        if (current is not null) batch.Add(LegacyEntry(session, ++sequence, currentTime, current));
                        current = line;
                        currentTime = timestamp;
                    }
                    else if (current is not null) current += "\n" + line;
                    else if (!string.IsNullOrWhiteSpace(line))
                        throw new InvalidDataException("旧运行日志的首条记录不完整");
                    if (batch.Count >= 256)
                    {
                        await SaveLegacyBatchAsync(batch).ConfigureAwait(false);
                        batch.Clear();
                    }
                }
                if (current is not null) batch.Add(LegacyEntry(session, ++sequence, currentTime, current));
                if (batch.Count > 0) await SaveLegacyBatchAsync(batch).ConfigureAwait(false);
            }
            var backup = path + ".migrated.bak";
            if (File.Exists(backup)) backup = path + "." + Guid.NewGuid().ToString("N") + ".migrated.bak";
            File.Move(path, backup);
        }
    }

    private async Task SaveLegacyBatchAsync(List<PendingEntry> batch)
    {
        foreach (var group in batch.GroupBy(row => row.Entry.Date))
            await WriteBatchAsync(group.Key, group.ToArray()).ConfigureAwait(false);
    }

    private PendingEntry LegacyEntry(string session, long sequence, DateTime timestamp, string line)
    {
        var parts = LogLine.Split(line);
        var date = DateOnly.FromDateTime(timestamp);
        var time = new DateTimeOffset(timestamp, _time.LocalTimeZone.GetUtcOffset(timestamp));
        // 旧日志只有短编号时保持空值，不能把短编号伪造成可关联的完整编号。
        var id = parts.Tags.FirstOrDefault(tag => tag.StartsWith("请求 ", StringComparison.Ordinal))?[3..];
        if (id is null || !Guid.TryParse(id, out _)) id = null;
        var route = parts.Source == LogSource.Preparation ? parts.Tags.ElementAtOrDefault(1) : parts.RouteName;
        return new PendingEntry(session, sequence, new RuntimeLogEntry(0, date, time.ToUnixTimeMilliseconds(),
            parts.Level switch { LogLevelFilter.Error => "ERROR", LogLevelFilter.Warning => "WARNING", _ => "INFO" },
            parts.Source, route ?? "", id, id is null ? null : date, line));
    }

    private static bool TryTimestamp(string line, out DateTime timestamp)
    {
        timestamp = default;
        return line.Length > 20 && DateTime.TryParseExact(line.AsSpan(0, 19), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out timestamp) && (line.AsSpan(20).StartsWith("INFO ", StringComparison.Ordinal)
                || line.AsSpan(20).StartsWith("WARNING ", StringComparison.Ordinal) || line.AsSpan(20).StartsWith("ERROR ", StringComparison.Ordinal));
    }
}
