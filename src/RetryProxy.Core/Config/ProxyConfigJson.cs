using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace RetryProxy.Core.Config;

/// <summary>
/// 解析结果：配置本身与"是否经过旧版迁移"。
/// </summary>
public readonly record struct ParsedProxyConfig(ProxyConfig Config, bool Migrated);

/// <summary>
/// ProxyConfig 与 JSON 之间的转换：读取时执行 schema 6 迁移，写出时使用固定键序的
/// snake_case 结构（与 Rust 版 canonical_value 完全一致）。
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
    /// 从已解析的 JSON 值构建配置并执行迁移（对应 Rust parse_config_value）。
    /// </summary>
    public static ParsedProxyConfig ParseValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new ConfigException("配置文件根节点必须是对象");
        }

        var defaults = new ProxyConfig();
        var schemaVersion = 0;
        if (value.TryGetProperty("schema_version", out var schemaElement)
            && schemaElement.ValueKind == JsonValueKind.Number
            && schemaElement.TryGetUInt64(out var rawSchema))
        {
            if (rawSchema > uint.MaxValue)
            {
                throw new ConfigException("配置版本超出支持范围");
            }

            schemaVersion = rawSchema > int.MaxValue ? int.MaxValue : (int)rawSchema;
        }

        var providerElements = new List<JsonElement>();
        var providers = new List<ProviderEndpoint>();
        if (value.TryGetProperty("providers", out var providersElement))
        {
            if (providersElement.ValueKind != JsonValueKind.Array)
            {
                throw new ConfigException("配置文件中的 providers 必须是数组");
            }

            var index = 0;
            foreach (var element in providersElement.EnumerateArray())
            {
                index++;
                providerElements.Add(element);
                providers.Add(ParseProvider(element, index));
            }
        }

        var hasRoutes = value.TryGetProperty("routes", out var routesElement);
        var routeElements = new List<JsonElement>();
        var routes = new List<ProxyRoute>();
        if (hasRoutes)
        {
            if (routesElement.ValueKind != JsonValueKind.Array)
            {
                throw new ConfigException("配置文件中的 routes 必须是数组");
            }

            var index = 0;
            foreach (var element in routesElement.EnumerateArray())
            {
                index++;
                routeElements.Add(element);
                routes.Add(ParseRoute(element, index, schemaVersion));
            }
        }

        var migratedKeepalive = false;
        for (var i = 0; i < routes.Count; i++)
        {
            var route = routes[i];
            var providerIndex = providers.FindIndex(provider =>
                string.Equals(route.ProviderName, provider.Name, StringComparison.OrdinalIgnoreCase));
            if (providerIndex >= 0)
            {
                migratedKeepalive |= InheritProviderKeepalive(route, routeElements[i], providerElements[providerIndex]);
            }
        }

        var listenPort = ToPort(
            Integer(value, "listen_port", defaults.ListenPort, "配置文件中的 listen_port"),
            "配置文件中的 listen_port必须是整数");
        var selectedRouteId = OptionalString(value, "selected_route_id", string.Empty);
        var timeoutSeconds = Number(value, "timeout_seconds", defaults.TimeoutSeconds, "配置文件中的 timeout_seconds");

        ClientType? legacyClientType = schemaVersion < ConfigDefaults.CurrentSchemaVersion
            ? ClientTypeExtensions.ForLegacyRoute(string.Empty, listenPort)
            : hasRoutes ? defaults.ClientType : null;

        var config = new ProxyConfig
        {
            UpstreamBaseUrl = OptionalString(value, "upstream_base_url", string.Empty),
            ClientType = ParseClientType(value, "配置文件", legacyClientType),
            ListenPort = listenPort,
            MaxRetries = ToLong(Integer(value, "max_retries", (ulong)defaults.MaxRetries, "配置文件中的 max_retries"), "配置文件中的 max_retries必须是整数"),
            TimeoutSeconds = timeoutSeconds,
            GenerationTimeoutSeconds = Number(value, "generation_timeout_seconds", ConfigDefaults.GenerationTimeoutSeconds, "配置文件中的 generation_timeout_seconds"),
            TotalTimeoutSeconds = Number(value, "total_timeout_seconds", Math.Max(ConfigDefaults.TotalTimeoutSeconds, timeoutSeconds), "配置文件中的 total_timeout_seconds"),
            BaseDelaySeconds = Number(value, "base_delay_seconds", defaults.BaseDelaySeconds, "配置文件中的 base_delay_seconds"),
            MaxDelaySeconds = Number(value, "max_delay_seconds", defaults.MaxDelaySeconds, "配置文件中的 max_delay_seconds"),
            DesiredRunning = Boolean(value, "desired_running", defaults.DesiredRunning, "配置文件中的 desired_running"),
            KeepaliveEnabled = defaults.KeepaliveEnabled,
            KeepaliveIdleMinutes = defaults.KeepaliveIdleMinutes,
            KeepaliveContextLimit = defaults.KeepaliveContextLimit,
            Providers = providers,
            Routes = routes,
            SelectedRouteId = selectedRouteId,
            SchemaVersion = ConfigDefaults.CurrentSchemaVersion,
            RuntimeOverrides = null,
        }.Normalize();

        var legacy = !hasRoutes && config.UpstreamBaseUrl.Length > 0;
        if (legacy && config.Routes.Count == 0)
        {
            var provider = config.Providers.FirstOrDefault(item => item.BaseUrl == config.UpstreamBaseUrl);
            if (provider is not null)
            {
                var route = new ProxyRoute
                {
                    Id = "legacy-default",
                    Name = "默认通道",
                    ProviderName = provider.Name,
                    ClientType = config.ClientType,
                    ListenPort = config.ListenPort,
                    MaxRetries = config.MaxRetries,
                    TimeoutSeconds = config.TimeoutSeconds,
                    GenerationTimeoutSeconds = config.GenerationTimeoutSeconds,
                    TotalTimeoutSeconds = config.TotalTimeoutSeconds,
                    BaseDelaySeconds = config.BaseDelaySeconds,
                    MaxDelaySeconds = config.MaxDelaySeconds,
                    DesiredRunning = config.DesiredRunning,
                };
                foreach (var providerElement in providerElements)
                {
                    if (providerElement.TryGetProperty("name", out var nameElement)
                        && nameElement.ValueKind == JsonValueKind.String
                        && string.Equals(nameElement.GetString()!.Trim(), route.ProviderName, StringComparison.OrdinalIgnoreCase))
                    {
                        InheritProviderKeepalive(route, null, providerElement);
                        break;
                    }
                }

                config.Routes.Add(route);
                config.SelectedRouteId = "legacy-default";
                config.Normalize();
            }
        }

        return new ParsedProxyConfig(
            config,
            legacy || migratedKeepalive || schemaVersion < ConfigDefaults.CurrentSchemaVersion);
    }

    /// <summary>
    /// 生成缩进的规范 JSON 文本（对应 Rust serde_json::to_string_pretty(canonical_value)）。
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
    /// 以固定键序写出配置。运行时覆盖不会被写入。
    /// </summary>
    public static void WriteCanonical(Utf8JsonWriter writer, ProxyConfig config)
    {
        var defaults = new ProxyConfig();
        string upstreamBaseUrl;
        int listenPort;
        long maxRetries;
        double timeoutSeconds;
        double generationTimeoutSeconds;
        double totalTimeoutSeconds;
        double baseDelaySeconds;
        double maxDelaySeconds;
        bool desiredRunning;
        var selected = config.SelectedRoute;
        if (selected is not null)
        {
            upstreamBaseUrl = config.ProviderByName(selected.ProviderName)?.BaseUrl ?? string.Empty;
            listenPort = selected.ListenPort;
            maxRetries = selected.MaxRetries;
            timeoutSeconds = selected.TimeoutSeconds;
            generationTimeoutSeconds = selected.GenerationTimeoutSeconds;
            totalTimeoutSeconds = selected.TotalTimeoutSeconds;
            baseDelaySeconds = selected.BaseDelaySeconds;
            maxDelaySeconds = selected.MaxDelaySeconds;
            desiredRunning = selected.DesiredRunning;
        }
        else if (config.RuntimeOverrides is not null)
        {
            upstreamBaseUrl = string.Empty;
            listenPort = defaults.ListenPort;
            maxRetries = defaults.MaxRetries;
            timeoutSeconds = defaults.TimeoutSeconds;
            generationTimeoutSeconds = defaults.GenerationTimeoutSeconds;
            totalTimeoutSeconds = defaults.TotalTimeoutSeconds;
            baseDelaySeconds = defaults.BaseDelaySeconds;
            maxDelaySeconds = defaults.MaxDelaySeconds;
            desiredRunning = false;
        }
        else
        {
            upstreamBaseUrl = config.UpstreamBaseUrl;
            listenPort = config.ListenPort;
            maxRetries = config.MaxRetries;
            timeoutSeconds = config.TimeoutSeconds;
            generationTimeoutSeconds = config.GenerationTimeoutSeconds;
            totalTimeoutSeconds = config.TotalTimeoutSeconds;
            baseDelaySeconds = config.BaseDelaySeconds;
            maxDelaySeconds = config.MaxDelaySeconds;
            desiredRunning = config.DesiredRunning;
        }

        writer.WriteStartObject();
        writer.WriteNumber("schema_version", ConfigDefaults.CurrentSchemaVersion);
        writer.WriteString("client_type", (selected?.ClientType ?? config.ClientType).AsStr());
        writer.WriteString("upstream_base_url", upstreamBaseUrl);
        writer.WriteNumber("listen_port", listenPort);
        writer.WriteNumber("max_retries", maxRetries);
        WriteDouble(writer, "timeout_seconds", timeoutSeconds);
        WriteDouble(writer, "generation_timeout_seconds", generationTimeoutSeconds);
        WriteDouble(writer, "total_timeout_seconds", totalTimeoutSeconds);
        WriteDouble(writer, "base_delay_seconds", baseDelaySeconds);
        WriteDouble(writer, "max_delay_seconds", maxDelaySeconds);
        writer.WriteBoolean("desired_running", desiredRunning);
        writer.WriteStartArray("providers");
        foreach (var provider in config.Providers)
        {
            writer.WriteStartObject();
            writer.WriteString("name", provider.Name);
            writer.WriteString("base_url", provider.BaseUrl);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteString("selected_route_id", config.SelectedRouteId);
        writer.WriteStartArray("routes");
        foreach (var route in config.Routes)
        {
            writer.WriteStartObject();
            writer.WriteString("id", route.Id);
            writer.WriteString("name", route.Name);
            writer.WriteString("provider_name", route.ProviderName);
            writer.WriteString("client_type", route.ClientType.AsStr());
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
            if (route.DedicatedPreparation)
            {
                writer.WriteBoolean("dedicated_preparation", true);
                writer.WriteString("protected_api_key", route.ProtectedApiKey);
                writer.WriteString("preparation_model", route.PreparationModel);
            }
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteDouble(Utf8JsonWriter writer, string name, double value)
    {
        // JSON 不能表示 NaN/Infinity；这类值在校验阶段已被拒绝，这里兜底写 0 以免抛异常。
        writer.WriteNumber(name, double.IsFinite(value) ? value : 0.0);
    }

    private static ProviderEndpoint ParseProvider(JsonElement value, int index)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new ConfigException($"配置文件中的 providers 第 {index} 项必须是对象");
        }

        var name = RequiredString(value, "name", $"配置文件中的 providers 第 {index} 项缺少 name");
        var baseUrl = RequiredString(value, "base_url", $"配置文件中的 providers 第 {index} 项缺少 base_url");
        return new ProviderEndpoint(name, baseUrl);
    }

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

    private static ProxyRoute ParseRoute(JsonElement value, int index, int schemaVersion)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new ConfigException($"配置文件中的 routes 第 {index} 项必须是对象");
        }

        var label = $"配置文件中的 routes 第 {index} 项";
        var id = RequiredString(value, "id", $"{label}缺少 id");
        var name = RequiredString(value, "name", $"{label}缺少 name");
        var providerName = RequiredString(value, "provider_name", $"{label}缺少 provider_name");
        var desiredRunning = Boolean(value, "desired_running", false, $"{label} desired_running");
        var timeoutSeconds = Number(value, "timeout_seconds", ConfigDefaults.TimeoutSeconds, $"{label} timeout_seconds");
        var listenPort = ToPort(
            Integer(value, "listen_port", ConfigDefaults.ListenPort, $"{label} listen_port"),
            $"{label} listen_port必须是整数");
        var clientType = ParseClientType(
            value,
            label,
            schemaVersion < ConfigDefaults.CurrentSchemaVersion
                ? ClientTypeExtensions.ForLegacyRoute(name, listenPort)
                : null);
        var route = new ProxyRoute
        {
            Id = id,
            Name = name,
            ProviderName = providerName,
            ClientType = clientType,
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
            DedicatedPreparation = Boolean(value, "dedicated_preparation", false, $"{label} dedicated_preparation"),
            ProtectedApiKey = value.TryGetProperty("protected_api_key", out _) ? OptionalString(value, "protected_api_key", string.Empty) : null,
            PreparationModel = value.TryGetProperty("preparation_model", out _) ? OptionalString(value, "preparation_model", string.Empty) : null,
        };
        route.NormalizeInPlace();
        return route;
    }

    /// <summary>
    /// 旧版把保活设置保存在服务商上；只填补通道尚未保存的字段。
    /// 返回是否有字段被继承。
    /// </summary>
    private static bool InheritProviderKeepalive(ProxyRoute route, JsonElement? routeValue, JsonElement providerValue)
    {
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

        var label = $"配置文件中的服务商“{route.ProviderName}”";
        route.KeepaliveEnabled = inherited.TryGetValue("keepalive_enabled", out var enabled)
            ? AsBoolean(enabled, $"{label} keepalive_enabled")
            : route.KeepaliveEnabled;
        route.KeepaliveIdleMinutes = inherited.TryGetValue("keepalive_idle_minutes", out var idle)
            ? AsNumber(idle, $"{label} keepalive_idle_minutes")
            : route.KeepaliveIdleMinutes;
        route.KeepaliveContextLimit = inherited.TryGetValue("keepalive_context_limit", out var limit)
            ? ToLong(AsInteger(limit, $"{label} keepalive_context_limit"), $"{label} keepalive_context_limit必须是整数")
            : route.KeepaliveContextLimit;
        return inherited.Count > 0;
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
