using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using RetryProxy.Core.Config;
using RetryProxy.Core.Logging;
using RetryProxy.Core.Storage;
using RetryProxy.Core.Workspace;
using RetryProxy.Service;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace RetryProxy.App.Tests;

/// <summary>完整经过配置服务、SQLite 和文件备份；隔离目录、环境与通知，不启动应用。</summary>
public sealed partial class ConfigServiceTests : IDisposable
{
    private const string Secret = "sk-config-regression-secret";
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "config-service-tests", Guid.NewGuid().ToString("N"));
    private readonly List<ConfigService> _services = new();
    private readonly ConcurrentQueue<string> _notifications = new();
    private readonly ProxyLogger _logger;
    private string UserDirectory => Path.Combine(_directory, "User");
    private string DbPath => Path.Combine(UserDirectory, "config.db");
    private string LegacyPath => Path.Combine(UserDirectory, "config.json");
    private string BackupDirectory => Path.Combine(UserDirectory, "backup");

    public ConfigServiceTests()
    {
        Directory.CreateDirectory(UserDirectory);
        _logger = ProxyLogger.Silent(Path.Combine(_directory, "logs"));
    }

    private ConfigService Open(IReadOnlyDictionary<string, string>? environment = null, Func<string?>? registryReader = null)
    {
        var service = new ConfigService(UserDirectory, () => environment ?? new Dictionary<string, string>(),
            registryReader ?? (() => null), _notifications.Enqueue, _logger);
        _services.Add(service);
        return service;
    }

    private static AllConfig Sample()
    {
        var proxy = ProxyConfig.Builtin();
        proxy.Providers.Add(new ProviderEndpoint
        {
            Id = "provider-1", Name = "测试供应商", BaseUrl = "https://example.test/v1", ClientType = ClientType.Codex,
            Keys =
            [
                new ProviderKey { Id = "key-1", Name = "当前 Key", ApiKey = "sk-current-key" },
                new ProviderKey { Id = "key-2", Name = "待删除 Key", ApiKey = Secret },
            ],
        });
        var route = proxy.RouteFor(ClientType.Codex)!;
        route.CurrentProviderId = "provider-1";
        route.CurrentKeyId = "key-1";
        return new AllConfig
        {
            Proxy = proxy,
            CommonConfig = new CommonConfig { RunForVersion = "before" },
            OtherConfig = new OtherConfig { UiCultureInfoName = "zh-Hans" },
            Preparations =
            [
                new SavedPreparation
                {
                    Id = "preparation-1", Number = 1, ClientType = "codex", ProviderSource = SavedPreparation.CustomSource,
                    ProviderUrl = "https://example.test/v1", ApiKey = "sk-preparation-secret", Model = "test-model",
                },
            ],
        };
    }

    private static Dictionary<string, string> Rows(AllConfig config) => new()
    {
        ["proxy"] = JsonSerializer.Serialize(config.Proxy, ConfigService.JsonOptions),
        ["common"] = JsonSerializer.Serialize(config.CommonConfig, ConfigService.JsonOptions),
        ["other"] = JsonSerializer.Serialize(config.OtherConfig, ConfigService.JsonOptions),
        ["preparations"] = JsonSerializer.Serialize(config.Preparations, ConfigService.JsonOptions),
    };

    private void Seed(Dictionary<string, string> rows)
    {
        using var database = new ConfigDatabase(UserDirectory);
        database.WriteRows(rows);
    }

    private SqliteConnection Connect(string? path = null, bool readOnly = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path ?? DbPath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private Dictionary<string, string> ReadRows(string? path = null)
    {
        using var connection = Connect(path, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM config ORDER BY key;";
        using var reader = command.ExecuteReader();
        var rows = new Dictionary<string, string>();
        while (reader.Read()) rows.Add(reader.GetString(0), reader.GetString(1));
        return rows;
    }

    private static void AssertRows(Dictionary<string, string> expected, Dictionary<string, string> actual)
    {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var row in expected) Assert.Equal(row.Value, actual[row.Key]);
    }

    private void AssertBlocked(ConfigService service)
    {
        service.Get().CommonConfig.RunForVersion = "不能保存";
        service.Save();
        Assert.Throws<ConfigException>(service.SaveChecked);
        Assert.Throws<ConfigException>(service.BackupBeforeDeletion);
        Assert.NotEmpty(_notifications);
    }

    private string[] Backups(string pattern = "config_*.json.bak") => Directory.Exists(BackupDirectory)
        ? Directory.GetFiles(BackupDirectory, pattern).Order(StringComparer.Ordinal).ToArray()
        : [];

    // 日志器和 SQLite 会保持写句柄；断言读取必须声明共享写，不能用 File.ReadAllText 的只共享读方式。
    private string ReadLog()
    {
        using var stream = new FileStream(_logger.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static byte[] ReadSharedBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private void BlockBackupDirectory() => File.WriteAllText(BackupDirectory, "占用备份目录名称");
    private static string LegacyJson(AllConfig config) => JsonSerializer.Serialize(config, ConfigService.JsonOptions);

    private ProxyWorkspace Workspace(ConfigService service)
    {
        var all = service.Get();
        return new ProxyWorkspace(_logger, all.Proxy!, candidate =>
        {
            var previous = all.Proxy;
            try
            {
                all.Proxy = candidate;
                service.SaveChecked();
            }
            catch
            {
                all.Proxy = previous;
                throw;
            }
        }) { BeforeDestructiveChange = service.BackupBeforeDeletion };
    }

    public void Dispose()
    {
        foreach (var service in _services) service.Dispose();
        _logger.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(_directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_directory, recursive: true);
    }
}
