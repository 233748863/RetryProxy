using System;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace RetryProxy.Core.Storage;

/// <summary>
/// 一个 SQLite 库文件的门面：建目录、统一连接参数（WAL、busy_timeout、synchronous=NORMAL）、
/// 建表与结构版本管理。读取随时 <see cref="Connect"/> 开短连接（连接池复用），写入必须经
/// <see cref="CreateWriteQueue"/> 排队串行执行。
/// 例：
/// <code>
/// var database = SqliteDatabase.Open(path, DatabaseSchemas.DataVersion, DatabaseSchemas.EnsureData);
/// using var queue = database.CreateWriteQueue();
/// await queue.ExecuteAsync(c =&gt; InsertDailyRequest(c, record));
/// </code>
/// </summary>
public sealed class SqliteDatabase
{
    private readonly string _connectionString;
    private readonly int _schemaVersion;
    private readonly Action<SqliteConnection, int> _ensureSchema;
    private readonly TimeProvider _clock;

    private SqliteDatabase(string path, string connectionString, int schemaVersion, Action<SqliteConnection, int> ensureSchema, TimeProvider clock)
    {
        Path = path;
        _connectionString = connectionString;
        _schemaVersion = schemaVersion;
        _ensureSchema = ensureSchema;
        _clock = clock;
    }

    /// <summary>库文件路径（绝对路径）。</summary>
    public string Path { get; }

    /// <summary>打开（必要时新建）库：建目录、开 WAL、建 meta 表、执行建表/升级回调并写入结构版本。</summary>
    /// <param name="ensureSchema">建表/升级回调，第二个参数为库中现有结构版本（新建为 0）。</param>
    /// <param name="clock">写入 meta.created_at 用的时钟；测试可注入。</param>
    public static SqliteDatabase Open(string path, int schemaVersion, Action<SqliteConnection, int> ensureSchema, TimeProvider? clock = null)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var directory = System.IO.Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            System.IO.Directory.CreateDirectory(directory);
        }

        var builder = new SqliteConnectionStringBuilder { DataSource = fullPath };
        var database = new SqliteDatabase(fullPath, builder.ToString(), schemaVersion, ensureSchema, clock ?? TimeProvider.System);
        database.EnsureSchema();
        return database;
    }

    /// <summary>打开一个已设置好 PRAGMA 的连接；用完即 Dispose（连接池会复用物理连接）。</summary>
    public SqliteConnection Connect()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;";
        command.ExecuteNonQuery();
        return connection;
    }

    /// <summary>为本库创建单写者写队列（每库一个，见 <see cref="SqliteWriteQueue"/>）。</summary>
    public SqliteWriteQueue CreateWriteQueue(int capacity = 1024) => new(this, capacity);

    /// <summary>当前库结构版本；空库返回 0。</summary>
    public int ReadSchemaVersion()
    {
        using var connection = Connect();
        return ReadSchemaVersion(connection);
    }

    private void EnsureSchema()
    {
        using var connection = Connect();
        // journal_mode 对库文件持久生效（写进文件头），重复设置没有副作用。
        Execute(connection, "PRAGMA journal_mode=WAL;");
        Execute(connection, "CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);");
        var version = ReadSchemaVersion(connection);
        if (version > _schemaVersion)
        {
            // 不回退：旧程序读新库只会读坏数据。
            throw new InvalidDataException($"数据库结构版本 {version} 高于当前程序支持的 {_schemaVersion}：{Path}");
        }

        if (version < _schemaVersion)
        {
            // 不用显式事务：建表语句幂等、版本号最后写，中途失败下次启动整体重跑。
            _ensureSchema(connection, version);
            WriteMeta(connection, "schema_version", _schemaVersion.ToString(CultureInfo.InvariantCulture));
            if (version == 0)
            {
                WriteMeta(connection, "created_at", _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
            }
        }
    }

    private static int ReadSchemaVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = 'schema_version';";
        var value = command.ExecuteScalar() as string;
        return value is not null && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static void WriteMeta(SqliteConnection connection, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO meta (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>清掉本库连接池里的物理连接（释放文件句柄；之后仍可继续使用）。</summary>
    public void Dispose()
    {
        using var probe = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(probe);
    }
}
