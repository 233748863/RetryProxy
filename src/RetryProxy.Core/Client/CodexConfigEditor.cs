using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using RetryProxy.Core.Config;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Syntax;

namespace RetryProxy.Core.Client;

/// <summary>
/// Codex 配置的纯文本读写器。只改接管和模型同步负责的键，不访问客户端目录或 auth.json。
/// 用 Tomlyn 无损语法树保留注释与排版，输出后再次解析并核对整份配置的语义。
/// </summary>
public sealed class CodexConfigEditor : IClientConfigEditor
{
    private const string InvalidToml = "Codex 配置格式无效，未修改文件；请检查 config.toml。";
    private const string UnsupportedLayout = "Codex 目标配置使用了内联表、点号键或不兼容的表结构，无法安全写入；请改为独立配置项和标准供应商段。";
    private const string InvalidField = "Codex 配置项类型无效，未修改文件；模型、地址和凭据应为字符串，上下文参数应为整数。";

    public ClientType ClientType => ClientType.Codex;

    public ClientProfile Read(string text, string? authText = null)
    {
        try
        {
            var model = Parse(text).ToModel();
            var providerName = SelectedProvider(model);
            var provider = Provider(model, providerName);
            var token = String(provider, "experimental_bearer_token");
            if (string.IsNullOrWhiteSpace(token)) token = ReadAuthKey(authText);
            return new ClientProfile
            {
                ClientType = ClientType,
                ProviderName = providerName,
                BaseUrl = String(provider, "base_url"),
                ApiKey = token,
                WireApi = String(provider, "wire_api"),
                HasConflictingSettings = HasConflicts(model, provider),
                Models = new ProviderModels
                {
                    Model = String(model, "model"),
                    ContextWindow = Integer(model, "model_context_window"),
                    AutoCompactTokenLimit = Integer(model, "model_auto_compact_token_limit"),
                },
            };
        }
        catch (ClientConfigException) { throw; }
        catch (Exception)
        {
            // 解析器异常可能带原始值；不能保留 Message 或 InnerException。
            throw new ClientConfigException(InvalidToml);
        }
    }

    public string Write(string text, ClientConfigRequest request)
    {
        try
        {
            if (request?.Channel is null || request.Channel.ClientType != ClientType)
                throw new ClientConfigException("Codex 配置写入请求的客户端类型无效。");

            var document = Parse(text);
            var expected = document.ToModel();
            var targets = ModelTargets(request.EffectiveModels, expected);
            if (!request.ModelsOnly)
            {
                var selected = SelectedProvider(expected);
                var providerName = selected == "openai" ? "retry_proxy" : selected;
                var provider = Provider(expected, providerName);
                RejectAuthenticationConflicts(provider);

                string baseUrl;
                string token;
                if (request.UseProxy)
                {
                    if (request.ListenPort is < 1 or > 65535)
                        throw new ConfigException("Codex 本地端口必须在 1 到 65535 之间");
                    if (!request.Channel.HasKey)
                        throw new ClientConfigException("当前 Codex 供应商没有 Key，无法接管。");
                    baseUrl = UrlRules.ClientBaseUrl(ClientType, $"http://127.0.0.1:{request.ListenPort}");
                    token = request.Channel.LocalToken;
                }
                else
                {
                    baseUrl = UrlRules.NormalizeBaseUrl(request.Channel.UpstreamBaseUrl);
                    UrlRules.ValidateBaseUrl(baseUrl, "Codex 上游地址");
                    baseUrl = UrlRules.CodexApiRoot(baseUrl);
                    token = request.Channel.ApiKey;
                }
                if (string.IsNullOrWhiteSpace(token))
                    throw new ClientConfigException("Codex 目标凭据为空，无法写入配置。");

                targets.Add(new(["model_provider"], providerName));
                targets.Add(new(["model_providers", providerName, "base_url"], baseUrl));
                targets.Add(new(["model_providers", providerName, "wire_api"], "responses"));
                targets.Add(new(["model_providers", providerName, "experimental_bearer_token"], token));
            }

            if (targets.Count == 0) return text;
            RejectProfileOverrides(expected, targets.Where(t => t.Path.Length == 1).Select(t => t.Path[0]));
            RejectUnsupportedLayouts(document, targets);
            var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            foreach (var target in targets)
            {
                SetSyntaxValue(document, target, newline);
                SetModelValue(expected, target);
            }

            var output = document.ToString();
            var actual = Parse(output).ToModel();
            // expected 只通过目标路径改变。比较整棵树，同时验证目标值和所有非目标值，不能仅比较数量。
            if (!SameSemantics(expected, actual))
                throw new ClientConfigException("Codex 配置写入校验失败，其他配置可能发生变化，未修改文件。");
            return output;
        }
        catch (ClientConfigException) { throw; }
        catch (ConfigException)
        {
            throw new ClientConfigException("Codex 写入参数无效；请检查端口和不含用户信息、查询参数或片段的上游地址。");
        }
        catch (Exception)
        {
            throw new ClientConfigException("Codex 配置无法安全写入，未修改文件；请检查配置格式及目标参数。");
        }
    }

    private static DocumentSyntax Parse(string text)
    {
        var document = Toml.Parse(text);
        if (document.HasErrors) throw new ClientConfigException(InvalidToml);
        ValidateIntegerRange(document);
        return document;
    }

    private static void ValidateIntegerRange(SyntaxNode node)
    {
        // Tomlyn 0.20 对部分超出 Int64 范围的整数会回绕；从整数词法单元核对范围，不能把损坏值当有效配置。
        if (node is IntegerValueSyntax integer)
        {
            try
            {
                var token = integer.Token!.Text!.Replace("_", "");
                var radix = token.StartsWith("0x", StringComparison.Ordinal) ? 16
                    : token.StartsWith("0o", StringComparison.Ordinal) ? 8
                    : token.StartsWith("0b", StringComparison.Ordinal) ? 2 : 10;
                var number = radix == 10
                    ? long.Parse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
                    : checked((long)Convert.ToUInt64(token[2..], radix));
                if (number != integer.Value) throw new ClientConfigException(InvalidToml);
            }
            catch (Exception)
            {
                throw new ClientConfigException(InvalidToml);
            }
        }
        for (var i = 0; i < node.ChildrenCount; i++)
            if (node.GetChild(i) is { } child) ValidateIntegerRange(child);
    }

    private static string SelectedProvider(TomlTable model)
    {
        var name = String(model, "model_provider");
        return string.IsNullOrWhiteSpace(name) ? "openai" : name;
    }

    private static TomlTable? Provider(TomlTable model, string name) => Table(Table(model, "model_providers"), name);

    private static TomlTable? Table(TomlTable? parent, string key)
    {
        if (parent is null || !parent.TryGetValue(key, out var value)) return null;
        return value as TomlTable ?? throw new ClientConfigException(UnsupportedLayout);
    }

    private static string String(TomlTable? table, string key)
    {
        if (table is null || !table.TryGetValue(key, out var value)) return string.Empty;
        return value as string ?? throw new ClientConfigException(InvalidField);
    }

    private static long? Integer(TomlTable table, string key)
    {
        if (!table.TryGetValue(key, out var value)) return null;
        return value is long number ? number : throw new ClientConfigException(InvalidField);
    }

    private static string ReadAuthKey(string? authText)
    {
        if (string.IsNullOrWhiteSpace(authText)) return string.Empty;
        try
        {
            using var auth = JsonDocument.Parse(authText);
            if (auth.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException();
            if (!auth.RootElement.TryGetProperty("OPENAI_API_KEY", out var key) || key.ValueKind == JsonValueKind.Null)
                return string.Empty;
            return key.GetString() ?? string.Empty;
        }
        catch (Exception)
        {
            throw new ClientConfigException("Codex 认证文件格式无效，无法读取 Key；请检查 auth.json。");
        }
    }

    private static List<Target> ModelTargets(ProviderModels models, TomlTable current)
    {
        var targets = new List<Target>();
        if (!string.IsNullOrWhiteSpace(models.Model)) targets.Add(new(["model"], models.Model.Trim()));
        if (models.ContextWindow is <= 0 || models.AutoCompactTokenLimit is <= 0)
            throw new ClientConfigException("Codex 上下文窗口和自动压缩阈值必须为正整数。");
        if (models.ContextWindow is { } context) targets.Add(new(["model_context_window"], context));
        if (models.AutoCompactTokenLimit is { } limit) targets.Add(new(["model_auto_compact_token_limit"], limit));
        if (models.ContextWindow.HasValue || models.AutoCompactTokenLimit.HasValue)
        {
            // 空项保留已有值，所以要验证合并后的窗口和阈值，例：只把窗口改小也不能小于已有阈值。
            var window = models.ContextWindow ?? Integer(current, "model_context_window");
            var compact = models.AutoCompactTokenLimit ?? Integer(current, "model_auto_compact_token_limit");
            if (window.HasValue && compact.HasValue && compact > window)
                throw new ClientConfigException("Codex 自动压缩阈值不能超过上下文窗口。");
        }
        return targets;
    }

    private static void RejectAuthenticationConflicts(TomlTable? provider)
    {
        if (provider is null) return;
        // env_key 即使是空串也可能触发环境变量取密钥；这些键不在本软件允许删除的范围内。
        if (provider.ContainsKey("env_key") || provider.ContainsKey("api_key")
            || provider.ContainsKey("auth") || provider.ContainsKey("aws") || provider.ContainsKey("gateway_oauth")
            || provider.TryGetValue("requires_openai_auth", out var requiresAuth) && requiresAuth is not false)
            throw new ClientConfigException("Codex 当前供应商存在环境变量、额外 Key、官方登录或其他认证配置，会与目标凭据冲突；请先手动处理，原配置未修改。");
        foreach (var name in new[] { "http_headers", "env_http_headers" })
        {
            var headers = Table(provider, name);
            if (headers?.Keys.Any(IsAuthenticationHeader) == true)
                throw new ClientConfigException("Codex 当前供应商存在认证请求头，会与目标凭据冲突；请先手动处理，原配置未修改。");
        }
    }

    private static bool IsAuthenticationHeader(string name) =>
        name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
        || name.Equals("x-api-key", StringComparison.OrdinalIgnoreCase)
        || name.Equals("api-key", StringComparison.OrdinalIgnoreCase);

    private static bool HasConflicts(TomlTable model, TomlTable? provider)
    {
        try
        {
            RejectAuthenticationConflicts(provider);
            // 检测状态时核对全部负责的顶层字段；即使本次没有同步模型，也不能把档案覆盖误判为已接管。
            RejectProfileOverrides(model, ["model_provider", "model", "model_context_window", "model_auto_compact_token_limit"]);
            return false;
        }
        catch (ClientConfigException)
        {
            return true;
        }
    }

    private static void RejectProfileOverrides(TomlTable model, IEnumerable<string> topLevelKeys)
    {
        var name = String(model, "profile");
        if (name.Length == 0) return;
        var profile = Table(Table(model, "profiles"), name);
        if (profile is not null && topLevelKeys.Any(profile.ContainsKey))
            throw new ClientConfigException("Codex 当前启用的配置档案覆盖了目标配置；请先手动处理档案，原配置未修改。");
    }

    private static void RejectUnsupportedLayouts(DocumentSyntax document, List<Target> targets)
    {
        CheckItems(document.KeyValues, []);
        foreach (var table in document.Tables)
        {
            var path = KeyPath(table.Name!);
            if (targets.Any(t => Prefix(t.Path, path) || table is TableArraySyntax && Prefix(path, t.Path)))
                throw new ClientConfigException(UnsupportedLayout);
            CheckItems(table.Items, path);
        }

        void CheckItems(SyntaxList<KeyValueSyntax> items, string[] tablePath)
        {
            foreach (var item in items)
            {
                var path = tablePath.Concat(KeyPath(item.Key!)).ToArray();
                if (!targets.Any(t => Prefix(path, t.Path) || Prefix(t.Path, path))) continue;
                if (item.Key!.DotKeys.ChildrenCount > 0 || item.Value is InlineTableSyntax
                    || targets.Any(t => Prefix(path, t.Path) && path.Length < t.Path.Length
                        || Prefix(t.Path, path) && t.Path.Length < path.Length))
                    throw new ClientConfigException(UnsupportedLayout);
            }
        }
    }

    private static string[] KeyPath(KeySyntax key) =>
        new[] { KeyPart(key.Key!) }.Concat(key.DotKeys.Select(p => KeyPart(p.Key!))).ToArray();

    private static string KeyPart(BareKeyOrStringValueSyntax part) => part switch
    {
        BareKeySyntax bare => bare.Key!.Text!,
        StringValueSyntax quoted => quoted.Value!,
        _ => throw new ClientConfigException(UnsupportedLayout),
    };

    private static bool Prefix(string[] prefix, string[] path) =>
        prefix.Length <= path.Length && prefix.SequenceEqual(path.Take(prefix.Length), StringComparer.Ordinal);

    private static void SetSyntaxValue(DocumentSyntax document, Target target, string newline)
    {
        var items = document.KeyValues;
        if (target.Path.Length > 1)
        {
            var parent = target.Path[..^1];
            var table = document.Tables.OfType<TableSyntax>().SingleOrDefault(t => KeyPath(t.Name!).SequenceEqual(parent, StringComparer.Ordinal));
            if (table is null)
            {
                var previous = (SyntaxNode?)document.Tables.LastOrDefault() ?? document.KeyValues.LastOrDefault();
                if (previous is not null) EnsureLineBreak(previous, newline);
                table = new TableSyntax(new KeySyntax(parent[0], parent[1]))
                {
                    EndOfLineToken = new SyntaxToken(TokenKind.NewLine, newline),
                };
                document.Tables.Add(table);
            }
            else if (table.EndOfLineToken is null)
            {
                // 文件可能停在 [供应商] # 注释；把尾注释放在换行前，避免新增项粘到表头或注释里。
                table.EndOfLineToken = new SyntaxToken(TokenKind.NewLine, newline) { LeadingTrivia = table.TrailingTrivia };
                table.TrailingTrivia = null;
            }
            items = table.Items;
        }

        var key = target.Path[^1];
        var existing = items.SingleOrDefault(item => item.Key!.DotKeys.ChildrenCount == 0 && KeyPart(item.Key.Key!) == key);
        if (existing is not null)
        {
            // 只换值的词法单元，保留该值、等号、行尾上的所有空白与注释。
            switch (existing.Value, target.Value)
            {
                case (StringValueSyntax node, string value):
                    if (node.Value == value) return;
                    var token = new StringValueSyntax(value).Token!;
                    node.Token!.Text = token.Text;
                    node.Token.TokenKind = token.TokenKind;
                    node.Value = value;
                    return;
                case (IntegerValueSyntax node, long value):
                    if (node.Value == value) return;
                    var number = new IntegerValueSyntax(value).Token!;
                    node.Token!.Text = number.Text;
                    node.Token.TokenKind = number.TokenKind;
                    node.Value = value;
                    return;
                default:
                    throw new ClientConfigException(InvalidField);
            }
        }

        if (items.LastOrDefault() is { } last) EnsureLineBreak(last, newline);
        ValueSyntax valueNode = target.Value is string s ? new StringValueSyntax(s) : new IntegerValueSyntax((long)target.Value);
        items.Add(new KeyValueSyntax(key, valueNode) { EndOfLineToken = new SyntaxToken(TokenKind.NewLine, newline) });
    }

    private static void EnsureLineBreak(SyntaxNode node, string newline)
    {
        if (!node.ToString().EndsWith('\n'))
            (node.TrailingTrivia ??= []).Add(new SyntaxTrivia(TokenKind.NewLine, newline));
    }

    private static void SetModelValue(TomlTable root, Target target)
    {
        var current = root;
        foreach (var key in target.Path[..^1])
        {
            var next = Table(current, key);
            if (next is null) current[key] = next = new TomlTable();
            current = next;
        }
        current[target.Path[^1]] = target.Value;
    }

    internal static bool SameSemantics(object? left, object? right)
    {
        if (left is TomlTable a && right is TomlTable b)
            return a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var value) && SameSemantics(pair.Value, value));
        if (left is IEnumerable aItems && right is IEnumerable bItems && left is not string && right is not string)
            return left.GetType() == right.GetType()
                && aItems.Cast<object>().SequenceEqual(bItems.Cast<object>(), SemanticComparer.Instance);
        return Equals(left, right);
    }

    private sealed class SemanticComparer : IEqualityComparer<object>
    {
        internal static readonly SemanticComparer Instance = new();
        public new bool Equals(object? x, object? y) => SameSemantics(x, y);
        public int GetHashCode(object obj) => 0;
    }

    private sealed record Target(string[] Path, object Value);
}
