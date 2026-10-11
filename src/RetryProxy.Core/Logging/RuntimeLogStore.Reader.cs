using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using RetryProxy.Core.Storage;

namespace RetryProxy.Core.Logging;

public sealed record RuntimeLogQuery(DateOnly Date)
{
    public LogSource? Source { get; init; }
    public string? Level { get; init; }
    public string? RouteName { get; init; }
    public string? PreparationSuffix { get; init; }
    public string Search { get; init; } = string.Empty;
    public int Offset { get; init; }
    public int PageSize { get; init; } = 200;
    public long? SnapshotId { get; init; }
    public long AfterId { get; init; }
}

public sealed record RuntimeLogPage(IReadOnlyList<RuntimeLogEntry> Entries, long Total, long SnapshotId, string? Warning);

public sealed partial class RuntimeLogStore
{
    /// <summary>首次恢复统计时读取新格式运行记录；旧文本的迁移副本由原文件或归档恢复，避免重复计数。</summary>
    internal static IEnumerable<string> ReadForStatistics(string directory, DateOnly date, string routeName)
    {
        using var files = new PartitionedDatabase(Path.Combine(directory, "runtime"), false, 1, EnsureSchema);
        using var lease = files.OpenRead(date);
        if (lease is null) yield break;
        using var command = lease.Connection.CreateCommand();
        command.CommandText = """
            SELECT line, request_id FROM runtime_logs
            WHERE source = $source AND route_name = $route AND session_id NOT LIKE 'legacy-%'
            ORDER BY id;
            """;
        command.Parameters.AddWithValue("$source", (int)LogSource.ChannelProxy);
        command.Parameters.AddWithValue("$route", routeName);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            yield return WithFullRequestId(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    public async Task<RuntimeLogPage> QueryAsync(RuntimeLogQuery query, CancellationToken token = default)
    {
        await FlushAsync(token).ConfigureAwait(false);
        return await Task.Run(() => ReadPage(query, token), token).ConfigureAwait(false);
    }

    private RuntimeLogPage ReadPage(RuntimeLogQuery query, CancellationToken token)
    {
        if (Volatile.Read(ref _cleanupEnabled) != 0 && query.Date < Cutoff) return new RuntimeLogPage([], 0, 0, Warning);
        using var lease = _files.OpenRead(query.Date, token);
        if (lease is null) return new RuntimeLogPage([], 0, 0, Warning);
        using var command = lease.Connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(id), 0) FROM runtime_logs;";
        var snapshot = query.SnapshotId ?? (long)command.ExecuteScalar()!;
        var where = Where(command, query, snapshot);
        command.CommandText = "SELECT COUNT(*) FROM runtime_logs WHERE " + where;
        var total = (long)command.ExecuteScalar()!;
        command.CommandText = "SELECT id, timestamp_ms, level, source, route_name, request_id, request_date, line FROM runtime_logs WHERE "
            + where + " ORDER BY id DESC LIMIT $limit OFFSET $offset;";
        command.Parameters.AddWithValue("$limit", Math.Clamp(query.PageSize, 1, 500));
        command.Parameters.AddWithValue("$offset", Math.Max(0, query.Offset));
        using var reader = command.ExecuteReader();
        var entries = new List<RuntimeLogEntry>();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            entries.Add(ReadEntry(reader, query.Date));
        }
        // 当前页从旧到新显示，与实时日志的滚动方向一致。
        entries.Reverse();
        return new RuntimeLogPage(entries, total, snapshot, Warning);
    }

    /// <summary>导出当前筛选的全部记录，以快照固定范围；逐行输出，不把整天日志加载到内存。</summary>
    public async Task ExportAsync(RuntimeLogQuery query, TextWriter output, CancellationToken token = default)
    {
        await FlushAsync(token).ConfigureAwait(false);
        await Task.Run(async () =>
        {
            if (Volatile.Read(ref _cleanupEnabled) != 0 && query.Date < Cutoff) return;
            using var lease = _files.OpenRead(query.Date, token);
            if (lease is null) return;
            using var command = lease.Connection.CreateCommand();
            command.CommandText = "SELECT COALESCE(MAX(id), 0) FROM runtime_logs;";
            var snapshot = query.SnapshotId ?? (long)command.ExecuteScalar()!;
            var where = Where(command, query, snapshot);
            command.CommandText = "SELECT id, timestamp_ms, level, source, route_name, request_id, request_date, line FROM runtime_logs WHERE "
                + where + " ORDER BY id;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                var entry = ReadEntry(reader, query.Date);
                await output.WriteLineAsync(WithFullRequestId(entry.Line, entry.RequestId).AsMemory(), token).ConfigureAwait(false);
            }
        }, token).ConfigureAwait(false);
    }

    private static string WithFullRequestId(string line, string? requestId)
        => requestId is { Length: > 0 } id
            ? line.Replace("[请求 " + RouteLogger.DisplayId(id) + "]", "[请求 " + id + "]", StringComparison.Ordinal)
            : line;

    private static RuntimeLogEntry ReadEntry(SqliteDataReader reader, DateOnly date)
        => new(reader.GetInt64(0), date, reader.GetInt64(1), reader.GetString(2), (LogSource)reader.GetInt32(3),
            reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : DateOnly.ParseExact(reader.GetString(6), "yyyy-MM-dd", CultureInfo.InvariantCulture), reader.GetString(7));

    private static string Where(SqliteCommand command, RuntimeLogQuery query, long snapshot)
    {
        var parts = new List<string> { "id <= $snapshot", "id > $after" };
        command.Parameters.AddWithValue("$snapshot", snapshot);
        command.Parameters.AddWithValue("$after", query.AfterId);
        if (query.Source is { } source)
        {
            parts.Add("source = $source");
            command.Parameters.AddWithValue("$source", (int)source);
        }
        if (!string.IsNullOrEmpty(query.Level))
        {
            parts.Add("level = $level");
            command.Parameters.AddWithValue("$level", query.Level);
        }
        if (query.RouteName is not null)
        {
            parts.Add("((source IN ($proxy, $keepalive) AND route_name = $route) OR (source = $preparation AND substr(route_name, -length($suffix)) = $suffix))");
            command.Parameters.AddWithValue("$proxy", (int)LogSource.ChannelProxy);
            command.Parameters.AddWithValue("$keepalive", (int)LogSource.ChannelKeepAlive);
            command.Parameters.AddWithValue("$preparation", (int)LogSource.Preparation);
            command.Parameters.AddWithValue("$route", query.RouteName);
            command.Parameters.AddWithValue("$suffix", query.PreparationSuffix ?? "\0");
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            parts.Add("(instr(lower(line), $search) > 0 OR instr(lower(COALESCE(request_id, '')), $search) > 0)");
            command.Parameters.AddWithValue("$search", query.Search.Trim().ToLowerInvariant());
        }
        return string.Join(" AND ", parts);
    }
}
