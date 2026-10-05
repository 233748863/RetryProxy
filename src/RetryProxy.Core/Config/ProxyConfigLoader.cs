using System;
using System.Collections.Generic;

namespace RetryProxy.Core.Config;

public enum ProxyConfigSource
{
    /// <summary>RETRY_PROXY_CONFIG_JSON 环境变量注入（测试用，保存为空操作）。</summary>
    EnvironmentInjection,

    /// <summary>User\config.db 中已有的代理配置。</summary>
    ConfigFile,

    /// <summary>首次启动时从注册表一次性导入的 Rust 版配置。</summary>
    Registry,

    /// <summary>内置默认配置。</summary>
    Builtin,
}

/// <summary>加载结果；<see cref="Notes"/> 是注入或注册表配置迁移时值得写日志的事项。</summary>
public sealed record ProxyConfigLoadResult(ProxyConfig Config, ProxyConfigSource Source, bool Migrated)
{
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 决定启动时采用哪份代理配置，并套用环境变量覆盖（对应 Rust load_persistent_config）。
/// </summary>
public static class ProxyConfigLoader
{
    public const string TestConfigEnv = "RETRY_PROXY_CONFIG_JSON";

    /// <summary>
    /// 是否处于测试注入模式。此模式下保存必须是空操作。
    /// </summary>
    public static bool IsTestInjectionActive(IReadOnlyDictionary<string, string>? environ = null)
    {
        environ ??= EnvironmentOverrides.CurrentEnvironment();
        return environ.ContainsKey(TestConfigEnv);
    }

    /// <summary>
    /// 按优先级选取配置：环境变量注入 → 已持久化的配置 → 注册表旧配置 → 内置默认；
    /// 随后校验并套用环境变量覆盖。
    /// </summary>
    /// <param name="persisted">config.db 中已读出的代理配置；没有已保存的配置时传 null。</param>
    /// <param name="environ">环境变量快照；null 表示当前进程环境。</param>
    /// <param name="registryReader">注册表读取函数，便于测试替换；null 使用真实注册表。</param>
    public static ProxyConfigLoadResult Load(
        ProxyConfig? persisted,
        IReadOnlyDictionary<string, string>? environ = null,
        Func<string?>? registryReader = null)
    {
        environ ??= EnvironmentOverrides.CurrentEnvironment();
        registryReader ??= RegistryConfigStore.ReadLegacyJson;

        ProxyConfig config;
        ProxyConfigSource source;
        var migrated = false;
        IReadOnlyList<string> notes = Array.Empty<string>();
        if (environ.TryGetValue(TestConfigEnv, out var injected))
        {
            var parsed = ProxyConfigJson.Parse(injected);
            (config, migrated, notes) = (parsed.Config, parsed.Migrated, parsed.Notes);
            source = ProxyConfigSource.EnvironmentInjection;
        }
        else if (persisted is not null)
        {
            config = persisted;
            source = ProxyConfigSource.ConfigFile;
        }
        else if (registryReader() is { } legacyJson)
        {
            var parsed = ProxyConfigJson.Parse(legacyJson);
            (config, migrated, notes) = (parsed.Config, parsed.Migrated, parsed.Notes);
            source = ProxyConfigSource.Registry;
        }
        else
        {
            config = ProxyConfig.Builtin();
            source = ProxyConfigSource.Builtin;
        }

        config.Validate(false);
        var overrides = EnvironmentOverrides.Resolve(config, environ);
        if (overrides is not null)
        {
            config.RuntimeOverrides = overrides;
            config.Normalize();
        }

        config.Validate(false);
        return new ProxyConfigLoadResult(config, source, migrated) { Notes = notes };
    }
}
