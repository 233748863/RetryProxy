using Microsoft.Data.Sqlite;

namespace RetryProxy.Core.Storage;

/// <summary>
/// 配置库、按月统计库与按日诊断库的表结构；兼容迁移旧 <c>logs\data.db</c>。
/// 版本号写在各自 meta 表的 schema_version 行，独立于配置 schema 7。
/// 例：新建库时回调收到 fromVersion=0，执行建表；将来结构升到 v2 时在方法里追加
/// <c>if (fromVersion &lt; 2) { /* ALTER ... */ }</c> 即可，其余代码不用动。
/// </summary>
public static class DatabaseSchemas
{
    /// <summary>配置库当前结构版本。</summary>
    public const int ConfigVersion = 1;

    /// <summary>旧综合数据库及诊断分库的结构版本。v3 增加按请求保存的诊断缺失标记。</summary>
    public const int DataVersion = 3;

    /// <summary>配置库建表/升级；幂等，可在已建好的库上重复执行。</summary>
    public static void EnsureConfig(SqliteConnection connection, int fromVersion)
    {
        if (fromVersion < 1)
        {
            // key 为 proxy / common / other / preparations，value 是各节点的现有 JSON 序列化。
            Execute(connection, """
                CREATE TABLE IF NOT EXISTS config (
                    key        TEXT PRIMARY KEY,
                    value      TEXT NOT NULL,
                    updated_at INTEGER NOT NULL
                );
                """);
        }
    }

    /// <summary>数据库建表/升级；幂等，可在已建好的库上重复执行。</summary>
    public static void EnsureData(SqliteConnection connection, int fromVersion)
    {
        EnsureStatistics(connection, fromVersion);
        EnsureDiagnostics(connection, fromVersion);
    }

    public static void EnsureStatistics(SqliteConnection connection, int fromVersion)
    {
        if (fromVersion >= 1) return;
        Execute(connection, """
                CREATE TABLE IF NOT EXISTS daily_requests (
                    route_id      TEXT NOT NULL,
                    date          TEXT NOT NULL,
                    request_id    TEXT NOT NULL,
                    updated_at_ms INTEGER NOT NULL,
                    sequence      INTEGER NOT NULL,
                    outcome       TEXT,
                    retry_count   INTEGER NOT NULL,
                    key_id        TEXT NOT NULL,
                    key_name      TEXT NOT NULL,
                    record_json   TEXT NOT NULL,
                    PRIMARY KEY (route_id, date, request_id)
                );
                CREATE INDEX IF NOT EXISTS ix_daily_requests_route_date ON daily_requests (route_id, date);
                CREATE TABLE IF NOT EXISTS daily_imports (
                    route_id TEXT NOT NULL,
                    date     TEXT NOT NULL,
                    PRIMARY KEY (route_id, date)
                );
                """);
    }

    public static void EnsureDiagnostics(SqliteConnection connection, int fromVersion)
    {
        if (fromVersion < 1)
        {
            Execute(connection, """
                CREATE TABLE IF NOT EXISTS diagnostic_events (
                    id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    session_id    TEXT NOT NULL,
                    request_id    TEXT NOT NULL,
                    date          TEXT NOT NULL,
                    sequence      INTEGER NOT NULL,
                    kind          TEXT NOT NULL,
                    entry_json    TEXT NOT NULL,
                    request_json  TEXT,
                    payload_bytes INTEGER NOT NULL,
                    created_at_ms INTEGER NOT NULL,
                    UNIQUE (session_id, request_id, sequence)
                );
                CREATE INDEX IF NOT EXISTS ix_diagnostic_events_date ON diagnostic_events (date, request_id, sequence);
                """);
        }

        if (fromVersion < 2)
        {
            // diagnostic_marks：某会话在某天的诊断记录不完整（对应旧 jsonl 的 "incomplete" 控制记录），
            // 重启后仍能把该会话当天已记录的请求标记为"诊断记录不完整"。
            Execute(connection, """
                CREATE TABLE IF NOT EXISTS diagnostic_marks (
                    session_id TEXT NOT NULL,
                    date       TEXT NOT NULL,
                    PRIMARY KEY (session_id, date)
                );
                CREATE INDEX IF NOT EXISTS ix_diagnostic_marks_date ON diagnostic_marks (date);
                """);
        }

        if (fromVersion < 3)
        {
            // 旧 diagnostic_marks 只证明当天有遗漏，不能据此判定每个请求都不完整。
            // 新标记记录确实丢失事件的请求；不保存正文、异常文本或额外身份信息。
            Execute(connection, """
                CREATE TABLE IF NOT EXISTS diagnostic_request_marks (
                    session_id TEXT NOT NULL,
                    request_id TEXT NOT NULL,
                    date       TEXT NOT NULL,
                    PRIMARY KEY (session_id, request_id, date)
                );
                CREATE INDEX IF NOT EXISTS ix_diagnostic_request_marks_date ON diagnostic_request_marks (date);
                """);
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
