using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using RetryProxy.Core.Cli;

namespace RetryProxy.Core.Config;

/// <summary>
/// 解析结果：配置本身与"是否经过旧版迁移（需要回写）"。<see cref="Notes"/> 是迁移过程中值得写日志的事项，
/// 例："每个客户端只保留一条通道，已移除：备用（Codex，端口 18082）"。
/// </summary>
public readonly record struct ParsedProxyConfig(ProxyConfig Config, bool Migrated)
{
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>
/// ProxyConfig 与 JSON 之间的转换。schema 7 直接读取；schema 6 及更早按 PRD-供应商管理 §8 迁移：
/// 服务商按引用它的通道拆到对应客户端，每个客户端保留一条通道并改名为客户端名。
/// 写出时使用固定键序的 snake_case 结构。
/// </summary>
public static class ProxyConfigJson
{
    private static readonly JsonWriterOptions PrettyWriterOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly ClientType[] ClientOrder = { ClientType.Codex, ClientType.Claude };

    /// <summary>
    /// 解析整份配置 JSON 文本。
    /// </summary>
    public static ParsedProxyConfig Parse(string text)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, DocumentOptions);
        }
        catch (JsonException error)
        {
            throw new ConfigException($"无法读取持久化配置：{error.Message}");
        }

        using (document)
        {
            return ParseValue(document.RootElement);
        }
    }

    /// <summary>
    /// 从已解析的 JSON 值构建配置，必要时执行迁移。
    /// </summary>
    public static ParsedProxyConfig ParseValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new ConfigException("配置文件根节点必须是对象");
        }

        var schemaVersion = SchemaVersionOf(value);
        if (schemaVersion < ConfigDefaults.CurrentSchemaVersion)
        {
            return ParseLegacy(value, schemaVersion);
        }

        var config = new ProxyConfig
        {
            Providers = ParseArray(value, "providers", ParseProvider)
                .Select((provider, position) => (provider.Provider, provider.SortIndex, position))
                .OrderBy(item => item.SortIndex ?? item.position)
                .Select(item => item.Provider)
                .ToList(),
            Routes = ParseArray(value, "routes", (element, index) => ParseRoute(element, index, null).Route),
            SelectedRouteId = OptionalString(value, "selected_route_id", string.Empty),
            ClientTakeover = ParseTakeover(value),
            SchemaVersion = ConfigDefaults.CurrentSchemaVersion,
        };
        config.Normalize();
        var changed = config.EnsureClientRoutes();
        return new ParsedProxyConfig(config, changed);
    }

    /// <summary>
    /// 生成缩进的规范 JSON 文本。
    /// </summary>
    public static string ToCanonicalJson(ProxyConfig config)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, PrettyWriterOptions))
        {
            WriteCanonical(writer, config);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// 解析规范 JSON 为 JsonDocument，供测试按键检查。
    /// </summary>
    public static JsonDocument ToCanonicalDocument(ProxyConfig config)
    {
        return JsonDocument.Parse(ToCanonicalJson(config));
    }

    /// <summary>
    /// 以固定键序写出配置。运行时覆盖不会被写入；顶层只保留选中通道 ID，其余参数都在各通道里。
    /// </summary>
    public static void WriteCanonical(Utf8JsonWriter writer, ProxyConfig config)
    {
        writer.WriteStartObject();
        writer.WriteNumber("schema_version", ConfigDefaults.CurrentSchemaVersion);
        writer.WriteString("selected_route_id", config.SelectedRouteId);
        writer.WriteStartArray("providers");
        var sortIndex = new Dictionary<ClientType, int>();
        foreach (var provider in config.Providers)
        {
            // sort_index 是同一客户端内的排列位置，例：Codex 的第 1、2 个供应商为 0、1。
            var position = sortIndex.GetValueOrDefault(provider.ClientType);
            sortIndex[provider.ClientType] = position + 1;
            WriteProvider(writer, provider, position);
        }

        writer.WriteEndArray();
        writer.WriteStartArray("routes");
        foreach (var route in config.Routes)
        {
            WriteRoute(writer, route);
        }

        writer.WriteEndArray();
        writer.WriteStartObject("client_takeover");
        foreach (var client in ClientOrder)
        {
            if (!config.ClientTakeover.TryGetValue(client, out var state))
            {
                continue;
            }

            writer.WriteStartObject(client.AsStr());
            writer.WriteBoolean("enabled", state.Enabled);
            writer.WriteString("config_path", state.ConfigPath);
            writer.WriteString("backup_path", state.BackupPath);
            writer.WriteString("last_written_hash", state.LastWrittenHash);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteProvider(Utf8JsonWriter writer, ProviderEndpoint provider, int sortIndex)
    {
        writer.WriteStartObject();
        writer.WriteString("id", provider.Id);
        writer.WriteString("client_type", provider.ClientType.AsStr());
        writer.WriteString("name", provider.Name);
        writer.WriteString("base_url", provider.BaseUrl);
        writer.WriteString("auth_mode", provider.AuthMode.AsConfigStr());
        writer.WriteStartObject("models");
        var models = provider.Models;
        writer.WriteString("model", models.Model);
        if (provider.ClientType == ClientType.Claude)
        {
            writer.WriteBoolean("context_1m", models.Context1M);
            WriteRole(writer, "opus", models.Opus);
            WriteRole(writer, "sonnet", models.Sonnet);
            WriteRole(writer, "haiku", models.Haiku);
            WriteRole(writer, "fable", models.Fable);
        }
        else
        {
            WriteOptionalLong(writer, "context_window", models.ContextWindow);
            WriteOptionalLong(writer, "auto_compact_token_limit", models.AutoCompactTokenLimit);
        }

        writer.WriteEndObject();
        writer.WriteString("website_url", provider.WebsiteUrl);
        writer.WriteString("notes", provider.Notes);
        writer.WriteStartObject("balance_query");
        writer.WriteString("mode", provider.BalanceQuery.Mode.AsStr());
        if (provider.BalanceQuery.Detected is { } detected)
        {
            writer.WriteString("detected", detected.AsStr());
        }
        else
        {
            writer.WriteNull("detected");
        }

        writer.WriteEndObject();
        writer.WriteNumber("sort_index", sortIndex);
        writer.WriteStartArray("keys");
        foreach (var key in provider.Keys)
        {
            writer.WriteStartObject();
            writer.WriteString("id", key.Id);
            writer.WriteString("name", key.Name);
            writer.WriteString("api_key", key.ApiKey);
            if (key.ModelOverride is { } modelOverride)
            {
                writer.WriteStartObject("model_override");
                writer.WriteString("model", modelOverride.Model);
                writer.WriteBoolean("context_1m", modelOverride.Context1M);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("model_override");
            }

            writer.WriteString("notes", key.Notes);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteRole(Utf8JsonWriter writer, string name, RoleModel role)
    {
        writer.WriteStartObject(name);
        writer.WriteString("model", role.Model);
        writer.WriteBoolean("context_1m", role.Context1M);
        writer.WriteEndObject();
    }

    private static void WriteOptionalLong(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void WriteRoute(Utf8JsonWriter writer, ProxyRoute route)
    {
        writer.WriteStartObject();
        writer.WriteString("id", route.Id);
        writer.WriteString("name", route.Name);
        writer.WriteString("client_type", route.ClientType.AsStr());
        writer.WriteString("current_provider_id", route.CurrentProviderId);
        writer.WriteString("current_key_id", route.CurrentKeyId);
        writer.WriteString("local_token", route.LocalToken);
        writer.WriteNumber("listen_port", route.ListenPort);
        writer.WriteNumber("max_retries", route.MaxRetries);
        WriteDouble(writer, "timeout_seconds", route.TimeoutSeconds);
        WriteDouble(writer, "generation_timeout_seconds", route.GenerationTimeoutSeconds);
        WriteDouble(writer, "total_timeout_seconds", route.TotalTimeoutSeconds);
        WriteDouble(writer, "base_delay_seconds", route.BaseDelaySeconds);
        WriteDouble(writer, "max_delay_seconds", route.MaxDelaySeconds);
        writer.WriteBoolean("desired_running", route.DesiredRunning);
        writer.WriteBoolean("keepalive_enabled", route.KeepaliveEnabled);
        WriteDouble(writer, "keepalive_idle_minutes", route.KeepaliveIdleMinutes);
        writer.WriteNumber("keepalive_context_limit", route.KeepaliveContextLimit);
        writer.WriteString("keepalive_reasoning_effort", route.KeepaliveReasoningEffort.AsStr());
        writer.WriteBoolean("pass_through_compression", route.PassThroughCompression);
        writer.WriteEndObject();
    }

    private static void WriteDouble(Utf8JsonWriter writer, string name, double value)
    {
        // JSON 不能表示 NaN/Infinity；这类值在校验阶段已被拒绝，这里兜底写 0 以免抛异常。
        writer.WriteNumber(name, double.IsFinite(value) ? value : 0.0);
    }

    // ---------------------------------------------------------------- schema 7 读取

    private static int SchemaVersionOf(JsonElement value)
    {
        if (value.TryGetProperty("schema_version", out var schemaElement)
            && schemaElement.ValueKind == JsonValueKind.Number
            && schemaElement.TryGetUInt64(out var rawSchema))
        {
            if (rawSchema > uint.MaxValue)
            {
                throw new ConfigException("配置版本超出支持范围");
            }

            return rawSchema > int.MaxValue ? int.MaxValue : (int)rawSchema;
        }

        return 0;
    }

    private static List<T> ParseArray<T>(JsonElement value, string key, Func<JsonElement, int, T> parse)
    {
        var result = new List<T>();
        if (!value.TryGetProperty(key, out var array))
        {
            return result;
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new ConfigException($"配置文件中的 {key} 必须是数组");
        }

        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            index++;
            result.Add(parse(element, index));
        }

        return result;
    }

    private static (ProviderEndpoint Provider, long? SortIndex) ParseProvider(JsonElement value, int index)
    {
        var label = $"配置文件中的 providers 第 {index} 项";
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new ConfigException($"{label}必须是对象");
        }

        var clientType = ParseClientType(value, label, null);
        var authText = OptionalString(value, "auth_mode", "bearer");
        var provider = new ProviderEndpoint
        {
            Id = RequiredString(value, "id", $"{label}缺少 id"),
            ClientType = clientType,
            Name = RequiredString(value, "name", $"{label}缺少 name"),
            BaseUrl = RequiredString(value, "base_url", $"{label}缺少 base_url"),
            AuthMode = ProviderOptionExtensions.ParseAuthMode(authText)
                ?? throw new ConfigException($"{label} auth_mode 只能是 bearer 或 x_api_key"),
            Models = ParseModels(value, label),
            WebsiteUrl = OptionalString(value, "website_url", string.Empty),
            Notes = OptionalString(value, "notes", string.Empty),
            BalanceQuery = ParseBalanceQuery(value, label),
            Keys = ParseArray(value, "keys", (element, keyIndex) => ParseKey(element, $"{label} keys 第 {keyIndex} 项")),
        };
        long? sortIndex = value.TryGetProperty("sort_index", out var sortElement)
            ? ToLong(AsInteger(sortElement, $"{label} sort_index"), $"{label} sort_index必须是整数")
            : null;
        provider.NormalizeInPlace();
        return (provider, sortIndex);
    }

    private static ProviderModels ParseModels(JsonElement value, string label)
    {
        var models = new ProviderModels();
        if (!value.TryGetProperty("models", out var element))
        {
            return models;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ConfigException($"{label} models 必须是对象");
        }

        models.Model = OptionalString(element, "model", string.Empty);
        models.Context1M = Boolean(element, "context_1m", false, $"{label} models.context_1m");
        models.Opus = ParseRole(element, "opus", label);
        models.Sonnet = ParseRole(element, "sonnet", label);
        models.Haiku = ParseRole(element, "haiku", label);
        models.Fable = ParseRole(element, "fable", label);
        models.ContextWindow = OptionalLong(element, "context_window", $"{label} models.context_window");
        models.AutoCompactTokenLimit = OptionalLong(element, "auto_compact_token_limit", $"{label} models.auto_compact_token_limit");
        return models;
    }

    private static RoleModel ParseRole(JsonElement models, string name, string label)
    {
        if (!models.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return new RoleModel();
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ConfigException($"{label} models.{name} 必须是对象");
        }

        return new RoleModel
        {
            Model = OptionalString(element, "model", string.Empty),
            Context1M = Boolean(element, "context_1m", false, $"{label} models.{name}.context_1m"),
        };
    }

    private static BalanceQuery ParseBalanceQuery(JsonElement value, string label)
    {
        if (!value.TryGetProperty("balance_query", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return new BalanceQuery();
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ConfigException($"{label} balance_query 必须是对象");
        }

        var mode = ProviderOptionExtensions.ParseBalanceMode(OptionalString(element, "mode", "auto"))
            ?? throw new ConfigException($"{label} balance_query.mode 无法识别");
        BalanceQueryMode? detected = null;
        if (element.TryGetProperty("detected", out var detectedElement) && detectedElement.ValueKind != JsonValueKind.Null)
        {
            // 记住的接口认不出来时当作尚未识别，下次查询重新识别。
            detected = detectedElement.ValueKind == JsonValueKind.String
                ? ProviderOptionExtensions.ParseBalanceMode(detectedElement.GetString())
                : null;
        }

        return new BalanceQuery { Mode = mode, Detected = detected };
    }

    private static ProviderKey ParseKey(JsonElement value, string label)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new ConfigException($"{label}必须是对象");
        }

        KeyModelOverride? modelOverride = null;
        if (value.TryGetProperty("model_override", out var overrideElement) && overrideElement.ValueKind != JsonValueKind.Null)
        {
            if (overrideElement.ValueKind != JsonValueKind.Object)
            {
                throw new ConfigException($"{label} model_override 必须是对象");
            }

            modelOverride = new KeyModelOverride
            {
                Model = OptionalString(overrideElement, "model", string.Empty),
                Context1M = Boolean(overrideElement, "context_1m", false, $"{label} model_override.context_1m"),
            };
        }

        var key = new ProviderKey
        {
            Id = RequiredString(value, "id", $"{label}缺少 id"),
            Name = RequiredString(value, "name", $"{label}缺少 name"),
            ApiKey = RequiredString(value, "api_key", $"{label}缺少 api_key"),
            ModelOverride = modelOverride,
            Notes = OptionalString(value, "notes", string.Empty),
        };
        key.NormalizeInPlace();
        return key;
    }

    private static Dictionary<ClientType, ClientTakeoverState> ParseTakeover(JsonElement value)
    {
        var result = new Dictionary<ClientType, ClientTakeoverState>();
        if (!value.TryGetProperty("client_takeover", out var element))
        {
            return result;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ConfigException("配置文件中的 client_takeover 必须是对象");
        }

        foreach (var client in ClientOrder)
        {
            if (!element.TryGetProperty(client.AsStr(), out var state))
            {
                continue;
            }

            var label = $"配置文件中的 client_takeover.{client.AsStr()}";
            if (state.ValueKind != JsonValueKind.Object)
            {
                throw new ConfigException($"{label}必须是对象");
            }

            result[client] = new ClientTakeoverState
            {
                Enabled = Boolean(state, "enabled", false, $"{label}.enabled"),
                ConfigPath = OptionalString(state, "config_path", string.Empty),
                BackupPath = OptionalString(state, "backup_path", string.Empty),
                LastWrittenHash = OptionalString(state, "last_written_hash", string.Empty),
            };
        }

        return result;
    }

    // ---------------------------------------------------------------- schema 6 及更早的迁移

    /// <summary>旧版通道：通道本身与它按名称引用的服务商。</summary>
    private sealed record LegacyRoute(ProxyRoute Route, string ProviderName);

    /// <summary>旧版服务商：只有名称与地址，保活字段可能还留在服务商上。</summary>
    private sealed record LegacyProvider(string Name, string BaseUrl, JsonElement? Element);

    private static ParsedProxyConfig ParseLegacy(JsonElement value, int schemaVersion)
    {
        var providers = ParseArray(value, "providers", ParseLegacyProvider);
        var hasRoutes = value.TryGetProperty("routes", out _);
        var routes = ParseArray(value, "routes", (element, index) =>
        {
            var parsed = ParseRoute(element, index, schemaVersion);
            var route = new LegacyRoute(parsed.Route, parsed.ProviderName!);
            if (FindLegacyProvider(providers, route.ProviderName) is { Element: { } providerElement })
            {
                InheritProviderKeepalive(route, element, providerElement);
            }

            return route;
        });

        var defaults = new ProxyRoute();
        var listenPort = ToPort(
            Integer(value, "listen_port", ConfigDefaults.ListenPort, "配置文件中的 listen_port"),
            "配置文件中的 listen_port必须是整数");
        var timeoutSeconds = Number(value, "timeout_seconds", defaults.TimeoutSeconds, "配置文件中的 timeout_seconds");
        var clientType = ParseClientType(
            value,
            "配置文件",
            schemaVersion < 6 ? ClientTypeExtensions.ForLegacyRoute(string.Empty, listenPort) : hasRoutes ? ClientType.Codex : null);
        var upstream = UrlRules.NormalizeBaseUrl(OptionalString(value, "upstream_base_url", string.Empty));
        var legacyRoute = new ProxyRoute
        {
            Id = "legacy-default",
            Name = "默认通道",
            ClientType = clientType,
            ListenPort = listenPort,
            MaxRetries = ToLong(Integer(value, "max_retries", (ulong)defaults.MaxRetries, "配置文件中的 max_retries"), "配置文件中的 max_retries必须是整数"),
            TimeoutSeconds = timeoutSeconds,
            GenerationTimeoutSeconds = Number(value, "generation_timeout_seconds", ConfigDefaults.GenerationTimeoutSeconds, "配置文件中的 generation_timeout_seconds"),
            TotalTimeoutSeconds = Number(value, "total_timeout_seconds", Math.Max(ConfigDefaults.TotalTimeoutSeconds, timeoutSeconds), "配置文件中的 total_timeout_seconds"),
            BaseDelaySeconds = Number(value, "base_delay_seconds", defaults.BaseDelaySeconds, "配置文件中的 base_delay_seconds"),
            MaxDelaySeconds = Number(value, "max_delay_seconds", defaults.MaxDelaySeconds, "配置文件中的 max_delay_seconds"),
            DesiredRunning = Boolean(value, "desired_running", defaults.DesiredRunning, "配置文件中的 desired_running"),
        };

        // 更早的单通道版本没有 routes：顶层字段就是唯一的通道，上游地址对应的服务商不存在时按主机名补建。
        if (!hasRoutes)
        {
            if (upstream.Length == 0 && providers.Count > 0)
            {
                upstream = providers[0].BaseUrl;
            }

            if (upstream.Length > 0)
            {
                var provider = providers.FirstOrDefault(item => item.BaseUrl == upstream);
                if (provider is null)
                {
                    var existing = providers.Select(item => new ProviderEndpoint(item.Name, item.BaseUrl));
                    provider = new LegacyProvider(UrlRules.GeneratedProviderName(upstream, existing), upstream, null);
                    providers.Add(provider);
                }

                var route = new LegacyRoute(legacyRoute, provider.Name);
                if (provider.Element is { } providerElement)
                {
                    InheritProviderKeepalive(route, null, providerElement);
                }

                routes.Add(route);
            }
        }

        var notes = new List<string>();
        var selectedRouteId = OptionalString(value, "selected_route_id", string.Empty).Trim();
        if (!hasRoutes && routes.Count > 0)
        {
            selectedRouteId = legacyRoute.Id;
        }

        var config = MigrateToCurrent(providers, routes, selectedRouteId, notes);
        return new ParsedProxyConfig(config, true) { Notes = notes };
    }

    /// <summary>
    /// 把旧版"按名称引用服务商、同一客户端可有多条通道"的结构转成 schema 7：
    /// <list type="number">
    /// <item>每个客户端保留一条通道：优先当前选中的，其次列表中的第一条；保留 ID（每日统计按它分目录），改名为客户端名。</item>
    /// <item>服务商按引用它的通道的客户端类型拆分，例：Codex 与 Claude Code 都用 "Any" → 各得一个 "Any"；
    /// 没有通道引用的服务商两个客户端都保留一份，避免丢失。拆出的服务商没有 Key。</item>
    /// </list>
    /// </summary>
    private static ProxyConfig MigrateToCurrent(List<LegacyProvider> providers, List<LegacyRoute> routes, string selectedRouteId, List<string> notes)
    {
        var selected = routes.FirstOrDefault(item => item.Route.Id == selectedRouteId);
        // 按引用比较：下面会改保留通道的名称，值相等比较会让哈希失效。
        var kept = new HashSet<LegacyRoute>(
            ClientOrder
                .Select(client => selected is not null && selected.Route.ClientType == client
                    ? selected
                    : routes.FirstOrDefault(item => item.Route.ClientType == client))
                .Where(item => item is not null)
                .Select(item => item!),
            ReferenceEqualityComparer.Instance);
        foreach (var dropped in routes.Where(item => !kept.Contains(item)))
        {
            notes.Add($"每个客户端只保留一条通道，已移除：{dropped.Route.Name}（{dropped.Route.ClientType.Label()}，端口 {dropped.Route.ListenPort}）");
        }

        var config = new ProxyConfig { SchemaVersion = ConfigDefaults.CurrentSchemaVersion };
        var providerIds = new Dictionary<(string, ClientType), string>();
        foreach (var provider in providers)
        {
            var clients = ClientOrder
                .Where(client => routes.Any(item => item.Route.ClientType == client && SameName(item.ProviderName, provider.Name)))
                .ToList();
            if (clients.Count == 0)
            {
                clients = ClientOrder.ToList();
                notes.Add($"服务商“{provider.Name}”没有通道在用，已同时保留到 Codex 与 Claude Code");
            }

            foreach (var client in clients)
            {
                var id = ProxyConfig.NewId();
                providerIds[(provider.Name.ToLowerInvariant(), client)] = id;
                config.Providers.Add(new ProviderEndpoint(provider.Name, provider.BaseUrl) { Id = id, ClientType = client });
            }
        }

        foreach (var item in routes.Where(kept.Contains).ToList())
        {
            var route = item.Route;
            route.Name = route.ClientType.Label();
            route.CurrentProviderId = providerIds.GetValueOrDefault((item.ProviderName.Trim().ToLowerInvariant(), route.ClientType), string.Empty);
            config.Routes.Add(route);
        }

        config.SelectedRouteId = selected is not null && kept.Contains(selected)
            ? selected.Route.Id
            : config.Routes.FirstOrDefault()?.Id ?? string.Empty;
        var before = config.Routes.Count;
        config.EnsureClientRoutes();
        foreach (var added in config.Routes.Skip(before))
        {
            notes.Add($"已为 {added.ClientType.Label()} 新建通道（端口 {added.ListenPort}）");
        }

        return config;
    }

    private static bool SameName(string left, string right) =>
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static LegacyProvider? FindLegacyProvider(List<LegacyProvider> providers, string name) =>
        providers.FirstOrDefault(provider => SameName(provider.Name, name));

    private static LegacyProvider ParseLegacyProvider(JsonElement value, int index)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new ConfigException($"配置文件中的 providers 第 {index} 项必须是对象");
        }

        var name = RequiredString(value, "name", $"配置文件中的 providers 第 {index} 项缺少 name").Trim();
        var baseUrl = RequiredString(value, "base_url", $"配置文件中的 providers 第 {index} 项缺少 base_url");
        return new LegacyProvider(name, UrlRules.NormalizeBaseUrl(baseUrl), value);
    }

    /// <summary>
    /// 旧版把保活设置保存在服务商上；只填补通道尚未保存的字段。
    /// </summary>
    private static void InheritProviderKeepalive(LegacyRoute legacy, JsonElement? routeValue, JsonElement providerValue)
    {
        var route = legacy.Route;
        var inherited = new Dictionary<string, JsonElement>();
        foreach (var key in new[] { "keepalive_enabled", "keepalive_idle_minutes", "keepalive_context_limit" })
        {
            var routeHasKey = routeValue is { ValueKind: JsonValueKind.Object } routeObject
                && routeObject.TryGetProperty(key, out _);
            if (!routeHasKey
                && providerValue.ValueKind == JsonValueKind.Object
                && providerValue.TryGetProperty(key, out var providerField))
            {
                inherited[key] = providerField;
            }
        }

        var label = $"配置文件中的服务商“{legacy.ProviderName}”";
        route.KeepaliveEnabled = inherited.TryGetValue("keepalive_enabled", out var enabled)
            ? AsBoolean(enabled, $"{label} keepalive_enabled")
            : route.KeepaliveEnabled;
        route.KeepaliveIdleMinutes = inherited.TryGetValue("keepalive_idle_minutes", out var idle)
            ? AsNumber(idle, $"{label} keepalive_idle_minutes")
            : route.KeepaliveIdleMinutes;
        route.KeepaliveContextLimit = inherited.TryGetValue("keepalive_context_limit", out var limit)
            ? ToLong(AsInteger(limit, $"{label} keepalive_context_limit"), $"{label} keepalive_context_limit必须是整数")
            : route.KeepaliveContextLimit;
    }

    // ---------------------------------------------------------------- 通道与基础类型

    private static ClientType ParseClientType(JsonElement value, string label, ClientType? legacyDefault)
    {
        if (value.TryGetProperty("client_type", out var element))
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                switch (element.GetString())
                {
                    case "codex":
                        return ClientType.Codex;
                    case "claude":
                        return ClientType.Claude;
                }
            }
        }
        else if (legacyDefault is { } fallback)
        {
            return fallback;
        }

        throw new ConfigException($"{label}必须指定客户端类型 client_type：codex 或 claude");
    }

    /// <summary>
    /// 解析一条通道。<paramref name="legacySchema"/> 为 null 表示 schema 7；否则按旧版读取 provider_name，
    /// 缺 client_type 时按名称与端口推断。
    /// </summary>
    private static (ProxyRoute Route, string? ProviderName) ParseRoute(JsonElement value, int index, int? legacySchema)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new ConfigException($"配置文件中的 routes 第 {index} 项必须是对象");
        }

        var label = $"配置文件中的 routes 第 {index} 项";
        var id = RequiredString(value, "id", $"{label}缺少 id");
        var name = RequiredString(value, "name", $"{label}缺少 name");
        var providerName = legacySchema is null ? null : RequiredString(value, "provider_name", $"{label}缺少 provider_name");
        var desiredRunning = Boolean(value, "desired_running", false, $"{label} desired_running");
        var timeoutSeconds = Number(value, "timeout_seconds", ConfigDefaults.TimeoutSeconds, $"{label} timeout_seconds");
        var listenPort = ToPort(
            Integer(value, "listen_port", ConfigDefaults.ListenPort, $"{label} listen_port"),
            $"{label} listen_port必须是整数");
        var clientType = ParseClientType(
            value,
            label,
            legacySchema is { } schema && schema < 6 ? ClientTypeExtensions.ForLegacyRoute(name, listenPort) : null);
        var route = new ProxyRoute
        {
            Id = id,
            Name = name,
            ClientType = clientType,
            CurrentProviderId = legacySchema is null ? OptionalString(value, "current_provider_id", string.Empty) : string.Empty,
            CurrentKeyId = legacySchema is null ? OptionalString(value, "current_key_id", string.Empty) : string.Empty,
            LocalToken = legacySchema is null ? OptionalString(value, "local_token", string.Empty) : string.Empty,
            ListenPort = listenPort,
            MaxRetries = ToLong(Integer(value, "max_retries", (ulong)ConfigDefaults.MaxRetries, $"{label} max_retries"), $"{label} max_retries必须是整数"),
            TimeoutSeconds = timeoutSeconds,
            GenerationTimeoutSeconds = Number(value, "generation_timeout_seconds", ConfigDefaults.GenerationTimeoutSeconds, $"{label} generation_timeout_seconds"),
            TotalTimeoutSeconds = Number(value, "total_timeout_seconds", Math.Max(ConfigDefaults.TotalTimeoutSeconds, timeoutSeconds), $"{label} total_timeout_seconds"),
            BaseDelaySeconds = Number(value, "base_delay_seconds", ConfigDefaults.BaseDelaySeconds, $"{label} base_delay_seconds"),
            MaxDelaySeconds = Number(value, "max_delay_seconds", ConfigDefaults.MaxDelaySeconds, $"{label} max_delay_seconds"),
            DesiredRunning = desiredRunning,
            KeepaliveEnabled = Boolean(value, "keepalive_enabled", false, $"{label} keepalive_enabled"),
            KeepaliveIdleMinutes = Number(value, "keepalive_idle_minutes", ConfigDefaults.KeepaliveIdleMinutes, $"{label} keepalive_idle_minutes"),
            KeepaliveContextLimit = ToLong(Integer(value, "keepalive_context_limit", (ulong)ConfigDefaults.KeepaliveContextLimit, $"{label} keepalive_context_limit"), $"{label} keepalive_context_limit必须是整数"),
            KeepaliveReasoningEffort = ReasoningEffortExtensions.Parse(OptionalString(value, "keepalive_reasoning_effort", "default")),
            PassThroughCompression = Boolean(value, "pass_through_compression", false, $"{label} pass_through_compression"),
        };
        route.NormalizeInPlace();
        return (route, providerName);
    }

    private static string RequiredString(JsonElement value, string key, string label)
    {
        if (value.TryGetProperty(key, out var element) && element.ValueKind == JsonValueKind.String)
        {
            return element.GetString()!;
        }

        throw new ConfigException($"{label}必须是字符串");
    }

    private static string OptionalString(JsonElement value, string key, string fallback)
    {
        if (!value.TryGetProperty(key, out var element))
        {
            return fallback;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString()!;
        }

        throw new ConfigException($"配置文件中的 {key} 必须是字符串");
    }

    private static long? OptionalLong(JsonElement value, string key, string label)
    {
        if (!value.TryGetProperty(key, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return ToLong(AsInteger(element, label), $"{label}必须是整数");
    }

    private static ulong Integer(JsonElement value, string key, ulong fallback, string label)
    {
        return value.TryGetProperty(key, out var element) ? AsInteger(element, label) : fallback;
    }

    private static ulong Integer(JsonElement value, string key, long fallback, string label)
    {
        return Integer(value, key, (ulong)fallback, label);
    }

    private static ulong AsInteger(JsonElement element, string label)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetUInt64(out var parsed))
        {
            return parsed;
        }

        throw new ConfigException($"{label}必须是整数");
    }

    private static double Number(JsonElement value, string key, double fallback, string label)
    {
        return value.TryGetProperty(key, out var element) ? AsNumber(element, label) : fallback;
    }

    private static double AsNumber(JsonElement element, string label)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var parsed))
        {
            return parsed;
        }

        throw new ConfigException($"{label}必须是数字");
    }

    private static bool Boolean(JsonElement value, string key, bool fallback, string label)
    {
        return value.TryGetProperty(key, out var element) ? AsBoolean(element, label) : fallback;
    }

    private static bool AsBoolean(JsonElement element, string label)
    {
        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ConfigException($"{label}必须是布尔值"),
        };
    }

    private static int ToPort(ulong value, string error)
    {
        // Rust 侧为 u32；超出范围视为"不是整数"，端口区间另由校验阶段判定。
        if (value > uint.MaxValue)
        {
            throw new ConfigException(error);
        }

        return value > int.MaxValue ? int.MaxValue : (int)value;
    }

    private static long ToLong(ulong value, string error)
    {
        if (value > long.MaxValue)
        {
            throw new ConfigException(error);
        }

        return (long)value;
    }
}
