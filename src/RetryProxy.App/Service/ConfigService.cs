using RetryProxy.Core.Config;
using RetryProxy.Core.Logging;
using RetryProxy.Service.Interface;
using RetryProxy.View.Windows;
using System;
using System.IO;
using System.Text.Json;
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
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(200);

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
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
                var config = Read();
                var importedFromRegistry = ResolveProxyConfig(config);
                Config = config;
                Config.OnAnyChangedAction = ScheduleSave;
                Config.InitEvent();
                if (importedFromRegistry)
                {
                    // 注册表只读一次；落盘后以 config.json 为准。
                    Write(config);
                }
            }

            return Config;
        }
    }

    /// <summary>
    /// 按"环境变量注入 → config.json → 注册表 → 内置默认"确定代理配置并套用环境变量覆盖。
    /// 返回是否来自注册表导入。
    /// </summary>
    private bool ResolveProxyConfig(AllConfig config)
    {
        ProxyConfigLoadResult loaded;
        try
        {
            loaded = ProxyConfigLoader.Load(config.Proxy);
        }
        catch (ConfigException error)
        {
            _proxyLogger?.Error($"代理配置无效，已改用内置默认配置：{error.Message}");
            ShowConfigExceptionDialog("读取", error);
            loaded = ProxyConfigLoader.Load(ProxyConfig.Builtin(), registryReader: () => null);
        }

        config.Proxy = loaded.Config;
        switch (loaded.Source)
        {
            case ProxyConfigSource.Registry:
                _proxyLogger?.Info("已从注册表导入旧配置");
                return true;
            case ProxyConfigSource.EnvironmentInjection:
                _proxyLogger?.Info($"已使用环境变量 {ProxyConfigLoader.TestConfigEnv} 注入的配置，本次运行不写入配置文件");
                break;
            case ProxyConfigSource.Builtin:
                _proxyLogger?.Info("未找到已保存的代理配置，已使用内置默认配置");
                break;
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

    public AllConfig Read()
    {
        _rwLock.EnterReadLock();
        var filePath = Global.Absolute(ConfigRelativePath);
        try
        {
            if (!File.Exists(filePath))
            {
                return new AllConfig();
            }

            var json = File.ReadAllText(filePath);
            var config = JsonSerializer.Deserialize<AllConfig>(json, JsonOptions);
            if (config == null)
            {
                return new AllConfig();
            }

            return config;
        }
        catch (Exception e)
        {
            Console.WriteLine(e.Message);
            Console.WriteLine(e.StackTrace);
            _proxyLogger?.Error($"配置文件读取失败，已备份后重建：{e.GetBaseException().Message}");
            BackupConfigFile(filePath);
            ShowConfigExceptionDialog("读取", e);
            return new AllConfig();
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    public void Write(AllConfig config)
    {
        // 自动化测试通过环境变量注入整份配置时，持久化必须保持无副作用。
        if (ProxyConfigLoader.IsTestInjectionActive())
        {
            return;
        }

        _rwLock.EnterWriteLock();
        var file = Global.Absolute(ConfigRelativePath);
        try
        {
            var path = Global.Absolute("User");
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            File.WriteAllText(file, JsonSerializer.Serialize(config, JsonOptions));
        }
        catch (Exception e)
        {
            Console.WriteLine(e.Message);
            Console.WriteLine(e.StackTrace);
            _proxyLogger?.Error($"配置文件写入失败：{e.GetBaseException().Message}");
            ShowConfigExceptionDialog("写入", e);
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    private static void BackupConfigFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return;
            }

            var directoryPath = Path.GetDirectoryName(filePath);
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                return;
            }

            var backupDirectory = Path.Combine(directoryPath, BackupFolderName);
            Directory.CreateDirectory(backupDirectory);

            var backupFileName = $"config_{DateTime.Now:yyyyMMdd_HHmmss_fff}.json.bak";
            var backupFilePath = Path.Combine(backupDirectory, backupFileName);
            File.Copy(filePath, backupFilePath, false);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            Console.WriteLine(ex.StackTrace);
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
        var message = $"配置文件{operation}失败\n错误：{coreException.Message}\n堆栈：\n{coreStack}";
        _ = ThemedMessageBox.ErrorAsync(message, "配置文件异常");
    }
}
