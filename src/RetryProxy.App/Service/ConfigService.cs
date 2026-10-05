using RetryProxy.Core.Config;
using RetryProxy.Core.Logging;
using RetryProxy.Core.Storage;
using RetryProxy.Service.Interface;
using RetryProxy.View.Windows;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using Application = System.Windows.Application;

namespace RetryProxy.Service;

/// <summary>
/// 配置读写：User\config.db 的 config 表四行 JSON（proxy / common / other / preparations，单事务覆盖）；
/// 库文件不存在或为空表时一次性导入旧 config.json（备份后导入，成功后把原文件改名为
/// config.json.migrated.bak）；破坏性操作前把全部配置导出为 User\backup 下的 JSON 备份（保留 5 份）。
/// 读取失败（库打不开 / 行缺失 / JSON 损坏）时备份库文件、阻止本次运行写库并提示（PRD-数据存储迁移 §2.1）。
/// </summary>
public class ConfigService : IConfigService, IDisposable
{
    private readonly object _locker = new();
    private readonly ProxyLogger? _proxyLogger;
    private readonly Timer _debounce;
    private readonly string _userDirectory;
    private readonly Func<IReadOnlyDictionary<string, string>> _environmentReader;
    private readonly Func<string?> _registryReader;
    private readonly Action<string> _notifyError;
    private AllConfig? _config;
    private bool _disposed;
    private DateTime _nextBackupTime;
    private const string UserRelativePath = "User";
    private const string ConfigDbRelativePath = @"User/config.db";
    private const string LegacyConfigRelativePath = @"User/config.json";
    private const string MigratedFileName = "config.json.migrated.bak";
    private const string BackupFolderName = "backup";
    private const int BackupsToKeep = 5;
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(200);

    private const string ProxyKey = "proxy";
    private const string CommonKey = "common";
    private const string OtherKey = "other";
    private const string PreparationsKey = "preparations";
    private static readonly string[] RequiredKeys = [ProxyKey, CommonKey, OtherKey, PreparationsKey];

    /// <summary>
    /// 读取配置失败时为 true：本次运行只用内存里的默认配置，不写库，避免覆盖库里的供应商与 Key
    /// （PRD-供应商管理 §8、PRD-数据存储迁移 §2.1）。用户修正或删除 config.db 后重启即可恢复。
    /// </summary>
    private bool _persistenceBlocked;

    private ConfigDatabase? _database;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>保留当前应用配置的只读入口；服务使用实例状态，避免不同目录相互串写。</summary>
    public static AllConfig? Config { get; private set; }

    private ConfigDatabase Database => _database ??= new ConfigDatabase(_userDirectory);

    public ConfigService(ProxyLogger? proxyLogger = null)
        : this(Global.Absolute(UserRelativePath), EnvironmentOverrides.CurrentEnvironment,
            RegistryConfigStore.ReadLegacyJson, ShowConfigExceptionDialog, proxyLogger)
    {
    }

    /// <summary>隔离目录、系统读取与通知边界；测试仍完整经过真实 SQLite 和文件备份逻辑。</summary>
    internal ConfigService(string userDirectory, Func<IReadOnlyDictionary<string, string>> environmentReader,
        Func<string?> registryReader, Action<string> notifyError, ProxyLogger? proxyLogger = null)
    {
        _userDirectory = Path.GetFullPath(userDirectory);
        _environmentReader = environmentReader;
        _registryReader = registryReader;
        _notifyError = notifyError;
        _proxyLogger = proxyLogger;
        _debounce = new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
    }

    private bool IsTestInjectionActive() => ProxyConfigLoader.IsTestInjectionActive(_environmentReader());
    private string LegacyFilePath => Path.Combine(_userDirectory, "config.json");

    public AllConfig Get()
    {
        lock (_locker)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_config == null)
            {
                var (config, persistedProxy, imported) = Read();
                var needsWrite = ResolveProxyConfig(config, persistedProxy);
                Config = _config = config;
                config.OnAnyChangedAction = ScheduleSave;
                config.InitEvent();
                if (needsWrite || imported)
                {
                    // 注册表只读一次、旧配置文件只导入一次；落库后以 config.db 为准。
                    // 导入成功（写入完成）后才改名旧文件，写入失败时下次启动会重试导入。
                    if (Write(config) && imported)
                    {
                        MarkLegacyFileMigrated();
                    }
                }
            }

            return _config;
        }
    }

    /// <summary>
    /// 按"环境变量注入 → 配置库 → 注册表 → 内置默认"确定代理配置并套用环境变量覆盖。
    /// 返回是否需要立即回写（来自注册表导入，或代理配置刚完成迁移）。
    /// </summary>
    private bool ResolveProxyConfig(AllConfig config, ParsedProxyConfig? persisted)
    {
        ProxyConfigLoadResult loaded;
        try
        {
            loaded = _persistenceBlocked
                ? ProxyConfigLoader.Load(ProxyConfig.Builtin(), _environmentReader(), registryReader: () => null)
                : ProxyConfigLoader.Load(persisted?.Config, _environmentReader(), _registryReader);
        }
        catch (ConfigException)
        {
            BlockPersistence("代理配置无效", BackupDatabaseFile());
            // 无效注入或环境覆盖也可能触发异常；恢复默认时不再次读取同一份无效输入。
            loaded = ProxyConfigLoader.Load(ProxyConfig.Builtin(), new Dictionary<string, string>(), () => null);
        }

        config.Proxy = loaded.Config;
        foreach (var note in loaded.Notes.Concat(persisted?.Notes ?? []))
        {
            _proxyLogger?.Info($"配置升级：{note}");
        }

        switch (loaded.Source)
        {
            case ProxyConfigSource.Registry:
                _proxyLogger?.Info("已从注册表导入旧配置");
                return true;
            case ProxyConfigSource.EnvironmentInjection:
                _proxyLogger?.Info($"已使用环境变量 {ProxyConfigLoader.TestConfigEnv} 注入的配置，本次运行不写入配置库");
                break;
            case ProxyConfigSource.Builtin:
                if (!_persistenceBlocked)
                {
                    _proxyLogger?.Info("未找到已保存的代理配置，已使用内置默认配置");
                }

                break;
            case ProxyConfigSource.ConfigFile when persisted?.Migrated == true:
                return true;
        }

        return false;
    }

    /// <summary>
    /// 任一属性变更后 200 ms 内合并为一次落库。
    /// </summary>
    public void ScheduleSave()
    {
        lock (_locker)
        {
            if (!_disposed) _debounce.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    public void Save()
    {
        lock (_locker)
        {
            if (_disposed) return;
            _debounce.Change(Timeout.Infinite, Timeout.Infinite);
            if (_config != null) Write(_config);
        }
    }

    /// <summary>管理供应商时保存失败必须返回给调用者，避免界面显示成功但密钥没有落库。</summary>
    public void SaveChecked()
    {
        lock (_locker)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _debounce.Change(Timeout.Infinite, Timeout.Infinite);
            if (_config is not null) Write(_config, throwOnError: true);
        }
    }

    /// <summary>先落库当前配置再导出 JSON 备份；备份失败则中止删除，例：删除 Key 前保留原密钥。</summary>
    public void BackupBeforeDeletion() => BackupBeforeChange("备份失败，已取消删除");

    public void BackupBeforeClientTakeover() => BackupBeforeChange("备份失败，已取消接管");

    private void BackupBeforeChange(string message)
    {
        lock (_locker)
        {
            if (IsTestInjectionActive()) return;
            SaveChecked();
            if (ExportBackup() is null) throw new ConfigException(message);
        }
    }

    /// <summary>
    /// 读取配置库。库文件不存在或为空表时尝试一次性导入旧 config.json；
    /// 读取失败（库打不开 / 行缺失 / JSON 损坏）时备份库文件、阻止本次运行写库并提示。
    /// </summary>
    private (AllConfig Config, ParsedProxyConfig? Proxy, bool Imported) Read()
    {
        try
        {
            var injection = IsTestInjectionActive();
            var dbPath = Database.Path;
            if (!File.Exists(dbPath))
            {
                // 注入模式不创建库（验收契约）；非注入时看有没有旧配置文件要一次性导入。
                return injection ? (new AllConfig(), null, false) : ImportLegacyConfig();
            }

            var rows = Database.ReadRows(readOnly: injection);
            if (rows.Count == 0)
            {
                return injection ? (new AllConfig(), null, false) : ImportLegacyConfig();
            }

            var missing = RequiredKeys.Where(key => !rows.ContainsKey(key)).ToArray();
            if (missing.Length > 0)
            {
                throw new InvalidDataException($"配置库缺少行：{string.Join("、", missing)}");
            }

            return ParseRows(rows);
        }
        catch (Exception)
        {
            BlockPersistence("配置库读取失败", BackupDatabaseFile());
            return (new AllConfig(), null, false);
        }
    }

    /// <summary>把配置库四行解析回配置对象；proxy 行按 ProxyConfigJson 解析（可能带 schema 迁移信息）。</summary>
    private static (AllConfig Config, ParsedProxyConfig? Proxy, bool Imported) ParseRows(Dictionary<string, string> rows)
    {
        // 既有库的 proxy 行必须是对象；JSON null 不能被当成首次启动而覆盖原库。
        if (JsonNode.Parse(rows[ProxyKey], documentOptions: DocumentOptions) is not JsonObject proxyNode)
        {
            throw new JsonException("代理配置节点必须是对象");
        }

        var proxy = ProxyConfigJson.ParseValue(JsonSerializer.SerializeToElement(proxyNode));

        var root = new JsonObject
        {
            ["commonConfig"] = JsonNode.Parse(rows[CommonKey], documentOptions: DocumentOptions),
            ["otherConfig"] = JsonNode.Parse(rows[OtherKey], documentOptions: DocumentOptions),
            ["preparations"] = JsonNode.Parse(rows[PreparationsKey], documentOptions: DocumentOptions),
        };
        var config = root.Deserialize<AllConfig>(JsonOptions) ?? new AllConfig();
        if (config.CommonConfig is null || config.OtherConfig is null)
        {
            throw new JsonException("配置节点缺失");
        }

        return (config, proxy, false);
    }

    /// <summary>
    /// 一次性导入旧配置文件：按现有解析与 schema 迁移逻辑读出配置，先按现规则备份原文件；
    /// 解析失败时备份原文件、阻止本次运行写库并提示（与旧版读取失败行为一致）。
    /// 落库与改名由调用方在写成功后执行。
    /// </summary>
    private (AllConfig Config, ParsedProxyConfig? Proxy, bool Imported) ImportLegacyConfig()
    {
        var filePath = LegacyFilePath;
        if (!File.Exists(filePath))
        {
            return (new AllConfig(), null, false);
        }

        try
        {
            var json = File.ReadAllText(filePath);
            if (JsonNode.Parse(json, documentOptions: DocumentOptions) is not JsonObject root)
            {
                throw new JsonException("配置文件根节点必须是对象");
            }

            ParsedProxyConfig? proxy = null;
            if (root.TryGetPropertyValue("proxy", out var proxyNode) && root.Remove("proxy") && proxyNode is not null)
            {
                proxy = ProxyConfigJson.ParseValue(JsonSerializer.SerializeToElement(proxyNode));
            }

            var config = root.Deserialize<AllConfig>(JsonOptions) ?? new AllConfig();
            if (config.CommonConfig is null || config.OtherConfig is null)
            {
                throw new JsonException("配置节点缺失");
            }

            var backup = BackupConfigFile(filePath);
            if (backup is null)
            {
                BlockPersistence("旧配置备份失败，已中止导入", null, legacyFile: true);
                return (new AllConfig(), null, false);
            }

            if (proxy?.Migrated == true)
            {
                _proxyLogger?.Info($"代理配置已升级到 schema {ConfigDefaults.CurrentSchemaVersion}，原文件已备份：{backup}");
            }

            _proxyLogger?.Info($"检测到旧配置文件，将导入配置库；原文件已备份：{backup}");
            return (config, proxy, true);
        }
        catch (Exception)
        {
            BlockPersistence("旧配置文件读取失败", BackupConfigFile(filePath), legacyFile: true);
            return (new AllConfig(), null, false);
        }
    }

    /// <summary>导入落库成功后把原文件改名为 config.json.migrated.bak（保留在磁盘，不再读写）。</summary>
    private void MarkLegacyFileMigrated()
    {
        try
        {
            var filePath = LegacyFilePath;
            if (File.Exists(filePath))
            {
                var target = Path.Combine(Path.GetDirectoryName(filePath)!, MigratedFileName);
                File.Move(filePath, target, overwrite: true);
            }
        }
        catch (Exception)
        {
            _proxyLogger?.Error("旧配置文件改名失败；已导入配置库，原文件仍保留，请检查文件权限");
        }
    }

    private void BlockPersistence(string reason, string? backup, bool legacyFile = false)
    {
        _persistenceBlocked = true;
        var source = legacyFile ? LegacyConfigRelativePath : ConfigDbRelativePath;
        var backupHint = backup is null
            ? $"备份未完成，请保留并检查 {source}"
            : "原文件已备份到 User/backup";
        // 配置异常可能含用户输入、密钥或原始 JSON；日志与界面只使用固定的操作提示。
        var message = $"{reason}。本次运行不会保存任何修改；{backupHint}；修正 {source} 后重启。";
        _proxyLogger?.Error(message);
        _notifyError(message);
    }

    /// <summary>
    /// 把四个配置节点在单事务里写入配置库。返回是否写入成功；
    /// 注入模式与读取失败（阻止写盘）时跳过并返回 false。
    /// </summary>
    private bool Write(AllConfig config, bool throwOnError = false)
    {
        // 自动化测试通过环境变量注入整份配置时，持久化必须保持无副作用；读取失败时不覆盖原库。
        if (IsTestInjectionActive()) return false;
        if (_persistenceBlocked)
        {
            if (throwOnError) throw new ConfigException("配置读取失败，本次运行不能保存修改");
            return false;
        }

        try
        {
            Database.WriteRows(SerializeRows(config));
            return true;
        }
        catch (Exception)
        {
            const string message = "配置保存失败，请检查 User/config.db 的文件权限与磁盘空间";
            _proxyLogger?.Error(message);
            if (throwOnError) throw new ConfigException(message);
            _notifyError(message);
            return false;
        }
    }

    private static Dictionary<string, string> SerializeRows(AllConfig config)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ProxyKey] = SerializeNode(config.Proxy),
            [CommonKey] = SerializeNode(config.CommonConfig),
            [OtherKey] = SerializeNode(config.OtherConfig),
            [PreparationsKey] = SerializeNode(config.Preparations),
        };
    }

    /// <summary>序列化单个配置节点；preparations 可为 null，其余必需节点由读取端校验。</summary>
    private static string SerializeNode<T>(T? value)
    {
        return JsonSerializer.SerializeToNode(value, JsonOptions)?.ToJsonString(JsonOptions) ?? "null";
    }

    /// <summary>
    /// 把当前内存里的全部配置导出为 User\backup\config_yyyyMMdd_HHmmss_fff.json.bak（人可读，与旧版命名一致），
    /// 只保留最近 5 份。返回备份路径；失败返回 null（调用方中止破坏性操作）。
    /// </summary>
    private string? ExportBackup()
    {
        try
        {
            if (_config is null)
            {
                return null;
            }

            var backupDirectory = BackupDirectoryPath();
            Directory.CreateDirectory(backupDirectory);
            var backupPath = NewBackupPath(backupDirectory, "json.bak");
            var temp = backupPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_config, JsonOptions));
            File.Move(temp, backupPath, overwrite: true);
            TrimBackups(backupDirectory, "config_*.json.bak");
            return backupPath;
        }
        catch (Exception)
        {
            _proxyLogger?.Error("配置导出备份失败，请检查备份目录权限与磁盘空间");
            return null;
        }
    }

    /// <summary>
    /// 把 config.json 复制到 User\backup\config_yyyyMMdd_HHmmss_fff.json.bak（一次性导入前、旧文件解析失败时用）。
    /// 返回备份路径；失败返回 null。
    /// </summary>
    private string? BackupConfigFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            var backupDirectory = BackupDirectoryPath();
            Directory.CreateDirectory(backupDirectory);
            var backupPath = NewBackupPath(backupDirectory, "json.bak");
            File.Copy(filePath, backupPath, overwrite: true);
            TrimBackups(backupDirectory, "config_*.json.bak");
            return backupPath;
        }
        catch (Exception)
        {
            _proxyLogger?.Error("旧配置文件备份失败，请检查备份目录权限与磁盘空间");
            return null;
        }
    }

    /// <summary>
    /// 优先生成包含 WAL 已提交事务的一致性备份；无法读取 SQLite 时，保留 db 与现存 wal 故障现场。
    /// 只有所有必需文件保存成功才返回路径，提示不能把备份失败说成成功。
    /// </summary>
    private string? BackupDatabaseFile()
    {
        string? backupPath = null;
        string? temp = null;
        try
        {
            var dbPath = Database.Path;
            if (!File.Exists(dbPath)) return null;

            var backupDirectory = BackupDirectoryPath();
            Directory.CreateDirectory(backupDirectory);
            backupPath = NewBackupPath(backupDirectory, "db.bak");
            temp = backupPath + ".tmp";
            try
            {
                Database.BackupTo(temp);
            }
            catch (Exception)
            {
                DeleteBackupFiles(temp);
                // shm 是可重建索引；已提交但未检查点的数据在 wal，故障现场必须一起保存。
                File.Copy(dbPath, temp);
                if (File.Exists(dbPath + "-wal")) File.Copy(dbPath + "-wal", temp + "-wal");
            }

            if (File.Exists(temp + "-wal")) File.Move(temp + "-wal", backupPath + "-wal");
            File.Move(temp, backupPath);
            TrimBackups(backupDirectory, "config_*.db.bak");
            return backupPath;
        }
        catch (Exception)
        {
            TryDeleteBackupFiles(temp);
            TryDeleteBackupFiles(backupPath);
            _proxyLogger?.Error("配置库备份失败，请保留原库及其日志文件，检查备份目录权限与磁盘空间");
            return null;
        }
    }

    private string BackupDirectoryPath() => Path.Combine(_userDirectory, BackupFolderName);

    private string NewBackupPath(string directory, string extension)
    {
        var time = DateTime.Now;
        if (time < _nextBackupTime) time = _nextBackupTime;
        string path;
        do
        {
            path = Path.Combine(directory, $"config_{time:yyyyMMdd_HHmmss_fff}.{extension}");
            time = time.AddMilliseconds(1);
        } while (File.Exists(path) || Directory.Exists(path));
        _nextBackupTime = time;
        return path;
    }

    /// <summary>文件名里的时间可按字典序排序；超出的旧备份及配套故障日志一起删除。</summary>
    private static void TrimBackups(string backupDirectory, string pattern)
    {
        foreach (var old in Directory.GetFiles(backupDirectory, pattern)
                     .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                     .Skip(BackupsToKeep))
        {
            DeleteBackupFiles(old);
        }
    }

    private static void DeleteBackupFiles(string path)
    {
        File.Delete(path + "-wal");
        File.Delete(path + "-shm");
        File.Delete(path);
    }

    private static void TryDeleteBackupFiles(string? path)
    {
        if (path is null) return;
        try { DeleteBackupFiles(path); }
        catch (Exception) { /* 清理失败不能遮住原来的备份错误。 */ }
    }

    /// <summary>关闭计时器与本实例的配置库；已排队的防抖回调不再写入。</summary>
    public void Dispose()
    {
        lock (_locker)
        {
            if (_disposed) return;
            _disposed = true;
            _debounce.Dispose();
            if (_config is not null) _config.OnAnyChangedAction = null;
            _database?.Dispose();
            if (ReferenceEquals(Config, _config)) Config = null;
        }
    }

    private static void ShowConfigExceptionDialog(string message)
    {
        if (Application.Current?.Dispatcher is null) return;
        _ = ThemedMessageBox.ErrorAsync(message, "配置文件异常");
    }
}
