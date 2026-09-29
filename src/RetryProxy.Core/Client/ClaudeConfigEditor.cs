using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RetryProxy.Core.Cli;
using RetryProxy.Core.Config;
using RetryProxy.Core.Proxy;

namespace RetryProxy.Core.Client;

/// <summary>
/// Claude Code 配置的纯文本读写器。只合并接管所需字段，不接触磁盘，也不执行 apiKeyHelper。
/// </summary>
public sealed class ClaudeConfigEditor : IClientConfigEditor
{
    private const string BaseUrlKey = "ANTHROPIC_BASE_URL";
    private const string AuthTokenKey = "ANTHROPIC_AUTH_TOKEN";
    private const string ApiKeyKey = "ANTHROPIC_API_KEY";
    private const string MainModelKey = "ANTHROPIC_MODEL";
    private const string MainModelMarker = "retry-proxy-main";
    private const string SubagentModelKey = "CLAUDE_CODE_SUBAGENT_MODEL";
    private const string ContextSuffix = "[1M]";

    // 固定角色名是客户端与代理之间的约定，不随供应商的真实模型名变化。
    private static readonly (string Keyword, string Key, string Official)[] Roles =
    [
        ("opus", "ANTHROPIC_DEFAULT_OPUS_MODEL", "claude-opus-5"),
        ("sonnet", "ANTHROPIC_DEFAULT_SONNET_MODEL", "claude-sonnet-5"),
        ("haiku", "ANTHROPIC_DEFAULT_HAIKU_MODEL", "claude-haiku-4-5"),
        ("fable", "ANTHROPIC_DEFAULT_FABLE_MODEL", "claude-fable-5"),
    ];

    private static readonly HashSet<string> ModelKeys = CreateModelKeys();
    private static readonly JsonSerializerOptions OutputOptions = new() { WriteIndented = true };

    public ClientType ClientType => ClientType.Claude;

    public ClientProfile Read(string text, string? authText = null)
    {
        var root = Parse(text);
        var env = root["env"] as JsonObject;
        var token = GetString(env, AuthTokenKey);
        var apiKey = GetString(env, ApiKeyKey);
        var hasToken = !string.IsNullOrWhiteSpace(token);
        var main = ReadModel(env, MainModelKey);
        return new ClientProfile
        {
            ClientType = ClientType,
            BaseUrl = GetString(env, BaseUrlKey),
            ApiKey = hasToken ? token : apiKey,
            AuthMode = hasToken || apiKey.Length == 0 ? ClaudeAuthMode.Bearer : ClaudeAuthMode.ApiKey,
            HasApiKeyHelper = root.ContainsKey("apiKeyHelper"),
            Models = new ProviderModels
            {
                Model = main.Model,
                Context1M = main.Context1M,
                Opus = ReadModel(env, Roles[0].Key),
                Sonnet = ReadModel(env, Roles[1].Key),
                Haiku = ReadModel(env, Roles[2].Key),
                Fable = ReadModel(env, Roles[3].Key),
            },
        };
    }

    public string Write(string text, ClientConfigRequest request)
    {
        var original = Parse(text);
        ValidateRequest(request);
        try
        {
            var updated = (JsonObject)original.DeepClone();
            var env = updated["env"] as JsonObject;
            var edits = ModelEdits(env, request);
            if (!request.ModelsOnly || edits.Count > 0)
            {
                if (env is null)
                {
                    env = new JsonObject();
                    updated["env"] = env;
                }

                if (!request.ModelsOnly)
                {
                    env[BaseUrlKey] = request.UseProxy
                        ? "http://127.0.0.1:" + request.ListenPort.ToString(CultureInfo.InvariantCulture)
                        : request.Channel.UpstreamBaseUrl;
                    var bearer = request.UseProxy || request.Channel.AuthMode == ClaudeAuthMode.Bearer;
                    env[bearer ? AuthTokenKey : ApiKeyKey] = request.UseProxy
                        ? request.Channel.LocalToken
                        : request.Channel.ApiKey;
                    env.Remove(bearer ? ApiKeyKey : AuthTokenKey);
                    updated.Remove("apiKeyHelper");
                }

                foreach (var (key, value) in edits)
                {
                    if (value is null)
                    {
                        env.Remove(key);
                    }
                    else
                    {
                        env[key] = value;
                    }
                }
            }

            var output = updated.ToJsonString(OutputOptions);
            // 重新解析实际输出再屏蔽允许修改的字段，防止序列化意外改变 permissions、hooks 等配置。
            EnsureUnrelatedFieldsUnchanged(original, Parse(output), request.ModelsOnly);
            return output;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        {
            // 解析器原始错误可能含密钥或配置片段；不保留原文，也不附带内部异常。
            throw new ClientConfigException("无法安全修改 Claude Code 配置，未返回修改结果。");
        }
    }

    private static Dictionary<string, string?> ModelEdits(JsonObject? env, ClientConfigRequest request)
    {
        var edits = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var role in Roles)
        {
            SetModel(role.Key, role.Official);
        }

        // 主模型使用不含角色关键词的独立标记，避免 Opus 等角色的覆盖抢走普通对话。
        // 例：主模型 claude-opus-5-5、Opus=glm-5，普通对话仍映射 claude-opus-5-5。
        // 主模型为空时不生成标记；已接管的旧标记按显示名恢复真实模型，避免内部标记外发。
        SetModel(MainModelKey, MainModelMarker);

        if (env?.ContainsKey(SubagentModelKey) == true)
        {
            var subagentRole = FindRole(GetString(env, SubagentModelKey));
            SetModel(SubagentModelKey, subagentRole is { } index ? Roles[index].Official : MainModelMarker, writeName: false);
        }

        return edits;

        void SetModel(string key, string marker, bool writeName = true)
        {
            // 与代理共用目标解析，独立角色优先于 Key 的主模型覆盖，1M 也从同一目标继承。
            if (ModelRewriter.ClaudeTarget(request.Channel, marker) is not { } target)
            {
                RestoreUnmappedModel(key, writeName);
                return;
            }

            edits[key] = (request.UseProxy ? marker : target.Model) + (target.Context1M ? ContextSuffix : string.Empty);
            if (writeName)
            {
                edits[key + "_NAME"] = target.Model;
            }
        }

        void RestoreUnmappedModel(string key, bool hasOwnName)
        {
            var existing = ReadModel(env, key);
            if (!IsManagedMarker(existing.Model))
            {
                return;
            }

            var nameKey = key + "_NAME";
            if (!hasOwnName)
            {
                // 子代理没有独立的显示名字段；按旧标记从主模型或相应角色的显示名恢复。
                var oldRole = FindRole(existing.Model);
                nameKey = (oldRole is { } index ? Roles[index].Key : MainModelKey) + "_NAME";
            }

            var previous = ReadModel(env, nameKey).Model;
            if (!string.IsNullOrWhiteSpace(previous) && !IsMainMarker(previous))
            {
                edits[key] = previous + (existing.Context1M ? ContextSuffix : string.Empty);
            }
            else if (IsMainMarker(existing.Model))
            {
                // 没有可恢复的真实名称时删掉内部标记，让客户端使用默认模型；官方模型仍可原样使用。
                edits[key] = null;
            }
        }
    }

    private static bool IsMainMarker(string model) => string.Equals(model, MainModelMarker, StringComparison.OrdinalIgnoreCase);

    private static bool IsManagedMarker(string model) => IsMainMarker(model)
        || Array.Exists(Roles, role => string.Equals(model, role.Official, StringComparison.OrdinalIgnoreCase));

    private static int? FindRole(string model)
    {
        for (var index = 0; index < Roles.Length; index++)
        {
            if (model.Contains(Roles[index].Keyword, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return null;
    }

    private static RoleModel ReadModel(JsonObject? env, string key)
    {
        var value = GetString(env, key);
        var context1M = value.EndsWith(ContextSuffix, StringComparison.OrdinalIgnoreCase);
        return new RoleModel
        {
            Model = context1M ? value[..^ContextSuffix.Length] : value,
            Context1M = context1M,
        };
    }

    private static string GetString(JsonObject? obj, string key) => obj?[key]?.GetValue<string>() ?? string.Empty;

    private static JsonObject Parse(string text)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new JsonObject();
            }

            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ClientConfigException("Claude Code 配置的根节点必须是对象。");
            }

            ValidateUniqueKeys(root);
            if (root.TryGetProperty("env", out var env))
            {
                if (env.ValueKind != JsonValueKind.Object)
                {
                    throw new ClientConfigException("Claude Code 配置的 env 必须是对象。");
                }

                foreach (var property in env.EnumerateObject())
                {
                    if (ModelKeys.Contains(property.Name) || IsConnectionKey(property.Name))
                    {
                        if (property.Value.ValueKind != JsonValueKind.String)
                        {
                            throw new ClientConfigException("Claude Code 配置中的地址、认证与模型字段必须是字符串。");
                        }

                        // 在脱敏边界内触发字符串解码，连孤立 UTF-16 代理项也不能泄露为解析器异常。
                        _ = property.Value.GetString();
                    }
                }
            }

            if (root.TryGetProperty("apiKeyHelper", out var helper) && helper.ValueKind != JsonValueKind.String)
            {
                throw new ClientConfigException("Claude Code 配置的 apiKeyHelper 必须是字符串。");
            }

            return (JsonObject)JsonNode.Parse(text)!;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new ClientConfigException("Claude Code 配置不是有效的 JSON，无法安全读取或修改。");
        }
    }

    private static void ValidateUniqueKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                // JsonObject 无法无损合并重名键；包括转义后同名的键，遇到歧义一律拒绝修改。
                if (!names.Add(property.Name))
                {
                    throw new ClientConfigException("Claude Code 配置包含重复字段，无法安全读取或修改。");
                }

                ValidateUniqueKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in element.EnumerateArray())
            {
                ValidateUniqueKeys(value);
            }
        }
    }

    private static void ValidateRequest(ClientConfigRequest request)
    {
        if (request?.Channel is null || request.Channel.ClientType != ClientType.Claude)
        {
            throw new ClientConfigException("写入目标必须是 Claude Code 通道。");
        }

        if (request.ModelsOnly)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Channel.ApiKey))
        {
            throw new ClientConfigException("当前供应商没有 Key，无法更新客户端配置。");
        }

        if (request.UseProxy)
        {
            if (request.ListenPort is < 1 or > 65535 || string.IsNullOrWhiteSpace(request.Channel.LocalToken))
            {
                throw new ClientConfigException("本机端口或本地口令无效，无法接管 Claude Code。");
            }
        }
        else if (string.IsNullOrWhiteSpace(request.Channel.UpstreamBaseUrl)
                 || request.Channel.AuthMode is not (ClaudeAuthMode.Bearer or ClaudeAuthMode.ApiKey))
        {
            throw new ClientConfigException("供应商地址或认证方式无效，无法恢复 Claude Code 直连。");
        }
    }

    private static bool IsConnectionKey(string key) => key is BaseUrlKey or AuthTokenKey or ApiKeyKey;

    private static HashSet<string> CreateModelKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal) { MainModelKey, MainModelKey + "_NAME", SubagentModelKey };
        foreach (var role in Roles)
        {
            keys.Add(role.Key);
            keys.Add(role.Key + "_NAME");
        }

        return keys;
    }

    private static void EnsureUnrelatedFieldsUnchanged(JsonObject before, JsonObject after, bool modelsOnly)
    {
        var beforeRest = (JsonObject)before.DeepClone();
        var afterRest = (JsonObject)after.DeepClone();
        Mask(beforeRest);
        Mask(afterRest);
        if (!before.ContainsKey("env") && afterRest["env"] is JsonObject { Count: 0 })
        {
            afterRest.Remove("env");
        }

        if (!JsonNode.DeepEquals(beforeRest, afterRest))
        {
            throw new ClientConfigException("Claude Code 配置中有非目标字段发生变化，已拒绝修改结果。");
        }

        void Mask(JsonObject root)
        {
            if (!modelsOnly)
            {
                root.Remove("apiKeyHelper");
            }

            if (root["env"] is not JsonObject env)
            {
                return;
            }

            foreach (var key in ModelKeys)
            {
                env.Remove(key);
            }

            if (!modelsOnly)
            {
                env.Remove(BaseUrlKey);
                env.Remove(AuthTokenKey);
                env.Remove(ApiKeyKey);
            }
        }
    }
}
