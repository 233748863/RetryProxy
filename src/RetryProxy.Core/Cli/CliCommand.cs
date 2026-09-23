using System;
using System.Collections.Generic;
using System.IO;
using RetryProxy.Core.KeepAlive;

namespace RetryProxy.Core.Cli;

/// <summary>CLI 调用失败（对应 Rust 侧的 Err(String)），消息可直接写日志。</summary>
public sealed class CliException : Exception
{
    public CliException(string message)
        : base(message)
    {
    }

    public CliException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>要拉起的本机 CLI：程序、固定参数与附加环境变量（对应 keepalive_cli.rs 的 CliCommand）。</summary>
public sealed class CliCommand
{
    public CliCommand(string program)
    {
        Program = program;
    }

    public string Program { get; }

    public List<string> Arguments { get; } = new();

    public List<KeyValuePair<string, string>> Environment { get; } = new();

    public CliCommand Clone()
    {
        var clone = new CliCommand(Program);
        clone.Arguments.AddRange(Arguments);
        clone.Environment.AddRange(Environment);
        return clone;
    }

    private static CliCommand Wrap(string path)
    {
        if (string.Equals(Path.GetExtension(path), ".ps1", StringComparison.OrdinalIgnoreCase))
        {
            var command = new CliCommand("powershell.exe");
            command.Arguments.AddRange(new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", path });
            return command;
        }

        return new CliCommand(path);
    }

    /// <summary>
    /// 定位本机 CLI：<c>RETRY_PROXY_CLAUDE_CLI</c> / <c>RETRY_PROXY_CODEX_CLI</c> 指定的绝对路径优先，
    /// 否则在 PATH 上找 <c>.exe/.cmd/.bat/.ps1</c>；<c>.ps1</c> 用 powershell 包装。
    /// </summary>
    public static CliCommand Discover(KeepAlive.KeepAliveFlavor flavor)
    {
        var (name, variable) = flavor == KeepAlive.KeepAliveFlavor.Claude
            ? ("claude", "RETRY_PROXY_CLAUDE_CLI")
            : ("codex", "RETRY_PROXY_CODEX_CLI");
        var configured = System.Environment.GetEnvironmentVariable(variable);
        if (configured is not null)
        {
            if (Path.IsPathRooted(configured) && File.Exists(configured))
            {
                return Wrap(configured);
            }

            throw new CliException($"{variable} 必须指向已安装 CLI 的绝对路径");
        }

        var path = System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", ".ps1" } : new[] { string.Empty };
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(directory.Trim('"'), name + extension);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (File.Exists(candidate))
                {
                    return Wrap(candidate);
                }
            }
        }

        throw new CliException($"未找到本机 {flavor.Label()} CLI，请先安装并完成配置");
    }
}

/// <summary>
/// 仅本次后台准备使用的密钥与入口地址。只注入到拉起的 CLI 子进程，不写入本机 CLI 配置、代理配置或日志。
/// </summary>
public sealed class CliCredential : IEquatable<CliCredential>
{
    private CliCredential(string apiKey, string baseUrl, string? model)
    {
        ApiKey = apiKey;
        BaseUrl = baseUrl;
        Model = model;
    }

    public string ApiKey { get; }

    /// <summary>代理本通道的本地监听地址，例如 <c>http://127.0.0.1:18081</c>。</summary>
    public string BaseUrl { get; }

    public string? Model { get; }

    /// <summary>校验并规范化；不合法时抛 <see cref="CliException"/>，消息可直接展示。</summary>
    public static CliCredential Create(string apiKey, string baseUrl, string? model = null)
    {
        apiKey = apiKey.Trim();
        if (apiKey.Length == 0)
        {
            throw new CliException("请输入用于准备的 API Key");
        }

        foreach (var character in apiKey)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                throw new CliException("API Key 不能包含空白或控制字符");
            }
        }

        baseUrl = baseUrl.Trim().TrimEnd('/');
        if (baseUrl.Length == 0)
        {
            throw new CliException("准备入口地址不能为空");
        }

        return new CliCredential(apiKey, baseUrl, model);
    }

    public static CliCredential CreateLocal(string baseUrl, string? model = null)
    {
        baseUrl = baseUrl.Trim().TrimEnd('/');
        if (baseUrl.Length == 0)
        {
            throw new CliException("准备入口地址不能为空");
        }

        return new CliCredential(string.Empty, baseUrl, model);
    }

    public bool Equals(CliCredential? other) => other is not null && ApiKey == other.ApiKey && BaseUrl == other.BaseUrl && Model == other.Model;

    public override bool Equals(object? obj) => Equals(obj as CliCredential);

    public override int GetHashCode() => HashCode.Combine(ApiKey, BaseUrl, Model);

    /// <summary>调试输出绝不带出密钥。</summary>
    public override string ToString() => $"CliCredential {{ ApiKey = <redacted>, BaseUrl = {BaseUrl}, Model = {Model} }}";
}
