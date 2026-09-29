using RetryProxy.Core.Config;
using RetryProxy.Core.Logging;
using RetryProxy.Service.Interface;
using RetryProxy.View.Windows;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using Application = System.Windows.Application;

namespace RetryProxy.Service;

public class ConfigService : IConfigService
{
    private readonly object _locker = new();
    private readonly ReaderWriterLockSlim _rwLock = new();
    private readonly ProxyLogger? _proxyLogger;
    private readonly Timer _debounce;
    private const string ConfigRelativePath = @"User/config.json";
    private const string BackupFolderName = "backup";
    private const int BackupsToKeep = 5;
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// 读取配置失败时为 true：本次运行只用内存里的默认配置，不写盘，避免覆盖原文件里的供应商与 Key
    /// （PRD-供应商管理 §8）。用户修正或删除 config.json 后重启即可恢复。
    /// </summary>
    private bool _persistenceBlocked;

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

    /// <summary>
    /// 写入只有UI线程会调用
    /// 多线程只会读，放心用static，不会丢失数据
    /// </summary>
    public static AllConfig? Config { get; private set; }

    public ConfigService(ProxyLogger? proxyLogger = null)
    {
        _proxyLogger = proxyLogger;
        _debounce = new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public AllConfig Get()
    {
        lock (_locker)
        {
            if (Config == null)
            {
                var (config, persistedProxy) = Read();
                var needsWrite = ResolveProxyConfig(config, persistedProxy);
                Config = config;
                Config.OnAnyChangedAction = ScheduleSave;
                Config.InitEvent();
                if (needsWrite)
                {
                    // 注册表只读一次、旧版配置只迁移一次；落盘后以 config.json 为准。
                    Write(config);
                }
            }

            return Config;
        }
    }

    /// <summary>
    /// 按"环境变量注入 → config.json → 注册表 → 内置默认"确定代理配置并套用环境变量覆盖。
    /// 返回是否需要立即回写（来自注册表导入，或 config.json 刚完成迁移）。
    /// </summary>
    private bool ResolveProxyConfig(AllConfig config, ParsedProxyConfig? persisted)
    {
        ProxyConfigLoadResult loaded;
        try
        {
            loaded = _persistenceBlocked
                ? ProxyConfigLoader.Load(ProxyConfig.Builtin(), registryReader: () => null)
                : ProxyConfigLoader.Load(persisted?.Config);
        }
        catch (ConfigException error)
        {
            BlockPersistence($"代理配置无效：{error.Message}");
            ShowConfigExceptionDialog("读取", error);
            loaded = ProxyConfigLoader.Load(ProxyConfig.Builtin(), registryReader: () => null);
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
                _proxyLogger?.Info($"已使用环境变量 {ProxyConfigLoader.TestConfigEnv} 注入的配置，本次运行不写入配置文件");
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
    /// 任一属性变更后 200 ms 内合并为一次落盘。
    /// </summary>
    public void ScheduleSave()
    {
        _debounce.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
    }

    public void Save()
    {
        _debounce.Change(Timeout.Infinite, Timeout.Infinite);
        if (Config != null)
        {
            Write(Config);
        }
    }

    /// <summary>
    /// 读取 config.json。代理节点单独解析，以便拿到"是否迁移"与迁移记录；旧版代理配置迁移前先备份原文件。
    /// 文件损坏时备份原文件、阻止本次运行写盘，并返回空配置。
    /// </summary>
    private (AllConfig Config, ParsedProxyConfig? Proxy) Read()
    {
        _rwLock.EnterReadLock();
        var filePath = Global.Absolute(ConfigRelativePath);
        try
        {
            if (!File.Exists(filePath))
            {
                return (new AllConfig(), null);
            }

            var json = File.ReadAllText(filePath);
            if (JsonNode.Parse(json, documentOptions: DocumentOptions) is not JsonObject root)
            {
                throw new JsonException("配置文件根节点必须是对象");
            }

            ParsedProxyConfig? proxy = null;
            if (root.TryGetPropertyValue("proxy", out var proxyNode) && root.Remove("proxy") && proxyNode is not null)
            {
                proxy = ProxyConfigJson.ParseValue(JsonSerializer.SerializeToElement(proxyNode));
                if (proxy.Value.Migrated)
                {
                    var backup = BackupConfigFile(filePath);
                    _proxyLogger?.Info($"代理配置已升级到 schema {ConfigDefaults.CurrentSchemaVersion}，原文件已备份：{backup ?? "备份失败"}");
                }
            }

            var config = root.Deserialize<AllConfig>(JsonOptions) ?? new AllConfig();
            return (config, proxy);
        }
        catch (Exception e)
        {
            Console.WriteLine(e.Message);
            Console.WriteLine(e.StackTrace);
            BackupConfigFile(filePath);
            BlockPersistence($"配置文件读取失败：{e.GetBaseException().Message}");
            ShowConfigExceptionDialog("读取", e);
            return (new AllConfig(), null);
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    private void BlockPersistence(string reason)
    {
        _persistenceBlocked = true;
        _proxyLogger?.Error($"{reason}。本次运行使用默认配置且不保存任何修改，原文件已备份；修正或删除 User\\config.json 后重启");
    }

    /// <summary>
    /// 先写临时文件再整体替换，写到一半断电或崩溃时原文件仍完整。
    /// </summary>
    private void Write(AllConfig config)
    {
        // 自动化测试通过环境变量注入整份配置时，持久化必须保持无副作用；读取失败时不覆盖原文件。
        if (ProxyConfigLoader.IsTestInjectionActive() || _persistenceBlocked)
        {
            return;
        }

        _rwLock.EnterWriteLock();
        var file = Global.Absolute(ConfigRelativePath);
        var temp = file + ".tmp";
        try
        {
            var path = Global.Absolute("User");
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            File.WriteAllText(temp, JsonSerializer.Serialize(config, JsonOptions));
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception e)
        {
            Console.WriteLine(e.Message);
            Console.WriteLine(e.StackTrace);
            _proxyLogger?.Error($"配置文件写入失败：{e.GetBaseException().Message}");
            TryDelete(temp);
            ShowConfigExceptionDialog("写入", e);
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // 临时文件删不掉不影响原文件，下次写入会覆盖它。
        }
    }

    /// <summary>
    /// 把 config.json 复制到 User\backup\config_yyyyMMdd_HHmmss_fff.json.bak，只保留最近 5 份。返回备份路径。
    /// </summary>
    private static string? BackupConfigFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            var directoryPath = Path.GetDirectoryName(filePath);
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                return null;
            }

            var backupDirectory = Path.Combine(directoryPath, BackupFolderName);
            Directory.CreateDirectory(backupDirectory);

            var backupFileName = $"config_{DateTime.Now:yyyyMMdd_HHmmss_fff}.json.bak";
            var backupFilePath = Path.Combine(backupDirectory, backupFileName);
            File.Copy(filePath, backupFilePath, false);

            // 文件名里的时间可按字典序排序；超出的旧备份删除。
            foreach (var old in Directory.GetFiles(backupDirectory, "config_*.json.bak")
                         .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                         .Skip(BackupsToKeep))
            {
                File.Delete(old);
            }

            return backupFilePath;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            Console.WriteLine(ex.StackTrace);
            return null;
        }
    }

    private static void ShowConfigExceptionDialog(string operation, Exception exception)
    {
        var current = Application.Current;
        if (current?.Dispatcher == null)
        {
            return;
        }

        var coreException = exception.GetBaseException();
        var coreStack = string.IsNullOrWhiteSpace(coreException.StackTrace) ? "无可用堆栈信息" : coreException.StackTrace;
        var hint = operation == "读取" ? "\n本次运行不会保存任何修改，原文件已备份到 User\\backup；修正或删除 User\\config.json 后重启。" : string.Empty;
        var message = $"配置文件{operation}失败\n错误：{coreException.Message}{hint}\n堆栈：\n{coreStack}";
        _ = ThemedMessageBox.ErrorAsync(message, "配置文件异常");
    }
}
