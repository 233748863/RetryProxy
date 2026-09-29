using System;
using RetryProxy.Core.Cli;

namespace RetryProxy.Core.Config;

/// <summary>余额查询方式（PRD-供应商管理 §4.6）。</summary>
public enum BalanceQueryMode
{
    /// <summary>依次尝试下面三种接口，记住能用的那一种。</summary>
    Auto,

    /// <summary><c>GET {查询地址}/v1/usage</c>。</summary>
    Usage,

    /// <summary><c>GET {查询地址}/user/balance</c>。</summary>
    UserBalance,

    /// <summary>OpenAI 兼容账单接口：subscription 的 hard_limit_usd 减 usage 的 total_usage ÷ 100。</summary>
    OpenAiBilling,

    /// <summary>不查询。</summary>
    None,
}

public static class ProviderOptionExtensions
{
    /// <summary>配置文件写法：Bearer → <c>bearer</c>（Authorization 头），ApiKey → <c>x_api_key</c>（x-api-key 头）。</summary>
    public static string AsConfigStr(this ClaudeAuthMode mode) => mode switch
    {
        ClaudeAuthMode.ApiKey => "x_api_key",
        _ => "bearer",
    };

    public static ClaudeAuthMode? ParseAuthMode(string? text) => text switch
    {
        "bearer" => ClaudeAuthMode.Bearer,
        "x_api_key" => ClaudeAuthMode.ApiKey,
        _ => null,
    };

    public static string AsStr(this BalanceQueryMode mode) => mode switch
    {
        BalanceQueryMode.Usage => "usage",
        BalanceQueryMode.UserBalance => "user_balance",
        BalanceQueryMode.OpenAiBilling => "openai_billing",
        BalanceQueryMode.None => "none",
        _ => "auto",
    };

    public static BalanceQueryMode? ParseBalanceMode(string? text) => text switch
    {
        "auto" => BalanceQueryMode.Auto,
        "usage" => BalanceQueryMode.Usage,
        "user_balance" => BalanceQueryMode.UserBalance,
        "openai_billing" => BalanceQueryMode.OpenAiBilling,
        "none" => BalanceQueryMode.None,
        _ => null,
    };
}

/// <summary>
/// 供应商的余额查询设置。<see cref="Detected"/> 只在 <see cref="Mode"/> 为自动识别时使用，
/// 记住上次识别成功的具体接口，为空表示尚未识别。
/// </summary>
public sealed class BalanceQuery : IEquatable<BalanceQuery>
{
    public BalanceQueryMode Mode { get; set; } = BalanceQueryMode.Auto;

    public BalanceQueryMode? Detected { get; set; }

    public BalanceQuery Clone() => new() { Mode = Mode, Detected = Detected };

    public bool Equals(BalanceQuery? other)
    {
        return other is not null && Mode == other.Mode && Detected == other.Detected;
    }

    public override bool Equals(object? obj) => Equals(obj as BalanceQuery);

    public override int GetHashCode() => HashCode.Combine(Mode, Detected);
}

/// <summary>
/// 某个客户端的接管状态（PRD-供应商管理 §4.4）。<see cref="Enabled"/> 是用户的持久选择：
/// 点「取消接管」后为 false，之后启动不再自动接管。
/// </summary>
public sealed class ClientTakeoverState : IEquatable<ClientTakeoverState>
{
    public bool Enabled { get; set; }

    /// <summary>接管时写入的客户端配置文件，例：<c>C:\Users\me\.claude\settings.json</c>。</summary>
    public string ConfigPath { get; set; } = string.Empty;

    /// <summary>首次接管前的备份目录。</summary>
    public string BackupPath { get; set; } = string.Empty;

    /// <summary>上次写入后文件的 SHA-256；与磁盘不一致说明被客户端或用户改过。</summary>
    public string LastWrittenHash { get; set; } = string.Empty;

    public ClientTakeoverState Clone() => (ClientTakeoverState)MemberwiseClone();

    public bool Equals(ClientTakeoverState? other)
    {
        return other is not null
            && Enabled == other.Enabled
            && ConfigPath == other.ConfigPath
            && BackupPath == other.BackupPath
            && LastWrittenHash == other.LastWrittenHash;
    }

    public override bool Equals(object? obj) => Equals(obj as ClientTakeoverState);

    public override int GetHashCode() => HashCode.Combine(Enabled, ConfigPath);
}
