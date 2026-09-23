using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using RetryProxy.Core.Cli;
using RetryProxy.Core.Config;

namespace RetryProxy.Core.Workspace;

internal static class LocalProviderCredentials
{
    private static readonly Regex Assignment = new(@"^\s*([A-Za-z_][A-Za-z_0-9]*)\s*=\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex ProviderTable = new(@"^model_providers\.(.+)$", RegexOptions.Compiled);

    public static CliCredential Read(ClientType clientType) => clientType == ClientType.Codex
        ? ReadCodex(Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } home
            ? home : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"))
        : ReadClaude(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"));

    internal static CliCredential ReadCodex(string directory)
    {
        string config;
        string auth;
        try
        {
            config = File.ReadAllText(Path.Combine(directory, "config.toml"));
            auth = File.ReadAllText(Path.Combine(directory, "auth.json"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new WorkspaceException("无法读取 .codex/config.toml 和 .codex/auth.json，请检查 CCC Switch 当前配置");
        }

        string? selected = null;
        var providers = new Dictionary<string, string>(StringComparer.Ordinal);
        string? section = null;
        foreach (var line in config.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('['))
            {
                section = trimmed.EndsWith(']') ? trimmed[1..^1].Trim() : string.Empty;
                continue;
            }

            var assignment = Assignment.Match(line);
            if (!assignment.Success)
            {
                continue;
            }

            var value = ReadTomlString(assignment.Groups[2].Value);
            if (section is null && assignment.Groups[1].Value == "model_provider")
            {
                selected = value;
            }
            else if (assignment.Groups[1].Value == "base_url" && ProviderTable.Match(section ?? string.Empty) is { Success: true } provider
                     && ReadTomlString(provider.Groups[1].Value) is { } providerName && value is not null)
            {
                providers[providerName] = value;
            }
        }

        try
        {
            using var document = JsonDocument.Parse(auth);
            var key = document.RootElement.TryGetProperty("OPENAI_API_KEY", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
            if (selected is null || !providers.TryGetValue(selected, out var baseUrl) || string.IsNullOrWhiteSpace(key))
            {
                throw new WorkspaceException(".codex 当前供应商缺少地址或 API Key，请检查 CCC Switch 配置");
            }

            return Validate(key, baseUrl);
        }
        catch (JsonException)
        {
            throw new WorkspaceException(".codex/auth.json 格式无效，请检查 CCC Switch 配置");
        }
    }

    internal static CliCredential ReadClaude(string directory)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "settings.json")));
            if (!document.RootElement.TryGetProperty("env", out var environment) || environment.ValueKind != JsonValueKind.Object)
            {
                throw new WorkspaceException(".claude/settings.json 缺少当前供应商配置");
            }

            var baseUrl = ReadJsonString(environment, "ANTHROPIC_BASE_URL");
            var token = ReadJsonString(environment, "ANTHROPIC_AUTH_TOKEN");
            var key = string.IsNullOrWhiteSpace(token) ? ReadJsonString(environment, "ANTHROPIC_API_KEY") : token;
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(key))
            {
                throw new WorkspaceException(".claude 当前供应商缺少地址或 API Key，请检查 CCC Switch 配置");
            }

            return Validate(key, baseUrl);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new WorkspaceException("无法读取 .claude/settings.json，请检查 CCC Switch 当前配置");
        }
    }

    private static string? ReadJsonString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? ReadTomlString(string raw)
    {
        var value = raw.Trim();
        if (value.StartsWith('"'))
        {
            var match = Regex.Match(value, "^\"(?:\\\\.|[^\"\\\\])*\"");
            if (match.Success)
            {
                try
                {
                    return JsonSerializer.Deserialize<string>(match.Value);
                }
                catch (JsonException)
                {
                    return null;
                }
            }
        }
        else if (value.StartsWith('\''))
        {
            var end = value.IndexOf('\'', 1);
            return end > 0 ? value[1..end] : null;
        }
        else
        {
            var match = Regex.Match(value, @"^[A-Za-z_0-9-]+");
            return match.Success ? match.Value : null;
        }

        return null;
    }

    private static CliCredential Validate(string key, string baseUrl)
    {
        try
        {
            var provider = new ProviderEndpoint("本机当前供应商", baseUrl);
            provider.Validate();
            return CliCredential.Create(key, provider.BaseUrl);
        }
        catch (Exception error) when (error is CliException or ConfigException)
        {
            throw new WorkspaceException("本机当前供应商的地址或密钥无效，请检查 CCC Switch 配置");
        }
    }
}
