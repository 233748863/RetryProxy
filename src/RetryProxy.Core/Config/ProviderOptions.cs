using System;
using RetryProxy.Core.Cli;

namespace RetryProxy.Core.Config;

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
