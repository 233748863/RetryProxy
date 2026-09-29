using System;
using System.Collections.Generic;
using System.Linq;
using RetryProxy.Core.Client;
using RetryProxy.Core.Config;
using Tomlyn;
using Tomlyn.Model;
using Xunit;

namespace RetryProxy.Tests;

public sealed class CodexConfigEditorTests
{
    private readonly CodexConfigEditor _editor = new();
    private const string LocalToken = "0123456789abcdef0123456789abcdef";
    private const string UpstreamKey = "sk-test-upstream-only";
    private const string Existing = "model_provider = \"relay\"\n[model_providers.relay]\nbase_url = \"https://old.example/v1\"\nexperimental_bearer_token = \"old-key\"\nwire_api = \"responses\"\n";

    private static ClientConfigRequest Request(bool proxy = true, bool modelsOnly = false, ProviderModels? models = null,
        string address = "https://upstream.example/", string key = UpstreamKey, string token = LocalToken, int port = 28080,
        KeyModelOverride? modelOverride = null, ClientType clientType = ClientType.Codex) => new()
    {
        UseProxy = proxy,
        ModelsOnly = modelsOnly,
        ListenPort = port,
        Channel = new ChannelSnapshot
        {
            ClientType = clientType,
            UpstreamBaseUrl = address,
            ApiKey = key,
            LocalToken = token,
            Models = models ?? new ProviderModels(),
            ModelOverride = modelOverride,
        },
    };

    [Fact]
    public void ReadUsesSelectedProviderAndTopLevelModels()
    {
        const string text = """
            model_provider = "chosen"
            model = 'main-model'
            model_context_window = 1_000_000
            model_auto_compact_token_limit = 900_000
            [model_providers.ignored]
            base_url = "https://ignored.example"
            experimental_bearer_token = "ignored-key"
            [model_providers.chosen]
            base_url = "https://chosen.example/codex"
            experimental_bearer_token = 'chosen-key'
            wire_api = "responses"
            model = "not-top-level"
            """;
        IClientConfigEditor editor = _editor;
        var profile = editor.Read(text, "{\"OPENAI_API_KEY\":\"auth-key\"}");
        Assert.Equal(ClientType.Codex, editor.ClientType);
        Assert.Equal(ClientType.Codex, profile.ClientType);
        Assert.Equal("chosen", profile.ProviderName);
        Assert.Equal("https://chosen.example/codex", profile.BaseUrl);
        Assert.Equal("chosen-key", profile.ApiKey);
        Assert.Equal("responses", profile.WireApi);
        Assert.Equal("main-model", profile.Models.Model);
        Assert.Equal(1_000_000L, profile.Models.ContextWindow);
        Assert.Equal(900_000L, profile.Models.AutoCompactTokenLimit);
        Assert.DoesNotContain("chosen-key", profile.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("model = 'model-only'")]
    [InlineData("model_provider = 'missing'")]
    [InlineData("# 只有注释\n")]
    public void ReadDoesNotRequireAuthFileOrCompleteProvider(string text)
    {
        var profile = _editor.Read(text);
        Assert.Equal(string.Empty, profile.BaseUrl);
        Assert.Equal(string.Empty, profile.ApiKey);
        Assert.Null(profile.Models.ContextWindow);
        Assert.Null(profile.Models.AutoCompactTokenLimit);
    }

    [Theory]
    [InlineData("", "auth-key")]
    [InlineData("experimental_bearer_token = ''", "auth-key")]
    [InlineData("experimental_bearer_token = '  '", "auth-key")]
    [InlineData("experimental_bearer_token = 'provider-key'", "provider-key")]
    public void ReadUsesAuthOnlyWhenSelectedProviderTokenIsEmpty(string entry, string expected)
    {
        var profile = _editor.Read("model_provider = 'p'\n[model_providers.p]\n" + entry, "{\"OPENAI_API_KEY\":\"auth-key\"}");
        Assert.Equal(expected, profile.ApiKey);
    }

    [Fact]
    public void ReadWithExplicitTokenDoesNotParseUnusedAuth()
    {
        Assert.Equal("old-key", _editor.Read(Existing, "invalid auth containing a secret").ApiKey);
        Assert.Equal("", _editor.Read("", "{\"OPENAI_API_KEY\":null}").ApiKey);
        Assert.Equal("", _editor.Read("", "{\"tokens\":{\"access_token\":\"not-an-api-key\"}}").ApiKey);
        Assert.Equal("openai", _editor.Read("").ProviderName);
    }

    [Theory]
    [InlineData("\"a.b 汉字\"", "\"a.b 汉字\"", "a.b 汉字")]
    [InlineData("'has space'", "'has space'", "has space")]
    [InlineData("'OpenAI'", "'OpenAI'", "OpenAI")]
    [InlineData("\"escaped\\\"name\"", "\"escaped\\\"name\"", "escaped\"name")]
    [InlineData("\"\\u0072elay\"", "'relay'", "relay")]
    public void QuotedProviderNamesAreDecodedWithoutSplittingDots(string selected, string section, string name)
    {
        var text = $"model_provider = {selected}\n[model_providers.{section}] # 段注释\nbase_url = 'https://old.example' # 地址\nexperimental_bearer_token = 'old'\n";
        var output = _editor.Write(text, Request());
        var profile = _editor.Read(output);
        Assert.Equal(name, profile.ProviderName);
        Assert.Equal("http://127.0.0.1:28080/v1", profile.BaseUrl);
        Assert.Equal(LocalToken, profile.ApiKey);
        Assert.Contains($"model_provider = {selected}", output);
        Assert.Contains($"[model_providers.{section}] # 段注释", output);
        Assert.Contains(" # 地址", output);
    }

    [Fact]
    public void ExistingCommentsWhitespaceSectionOrderAndUnrelatedSyntaxAreExact()
    {
        const string text = """
            # 首注释
            model_provider  =  'relay' # 保留选择
            model = 'before-model' # 模型
            model_context_window = 128_000 # 窗口
            # 工具表在供应商之前
            [mcp_servers.test]
            command = 'some-tool'
            args = ["--port", "28099"]
            [model_providers.relay] # 当前供应商
            name = '显示名称'
            base_url  =  'https://old.example/v1' # 地址
            experimental_bearer_token = 'old-key' # 认证
            wire_api = 'chat' # 协议
            requires_openai_auth = false
            request_max_retries = 2
            # 其余段落
            [model_providers.untouched]
            base_url = 'https://untouched.example'
            experimental_bearer_token = 'untouched-key'
            [features]
            thing = true
            """;
        var expected = text.Replace("'before-model'", "\"after-model\"")
            .Replace("128_000 # 窗口", "256000 # 窗口")
            .Replace("'https://old.example/v1'", "\"http://127.0.0.1:28080/v1\"")
            .Replace("'old-key'", $"\"{LocalToken}\"")
            .Replace("'chat'", "\"responses\"");
        var output = _editor.Write(text, Request(models: new() { Model = "after-model", ContextWindow = 256_000 }));
        Assert.Equal(expected, output);
        Assert.Equal(output, _editor.Write(output, Request(models: new() { Model = "after-model", ContextWindow = 256_000 })));
    }

    [Theory]
    [InlineData("")]
    [InlineData("# 仅注释，无结尾换行")]
    [InlineData("# 首注释\r\n")]
    [InlineData("model_provider = 'openai'\n[model_providers.openai]\nname = 'OpenAI'\n")]
    [InlineData("model_provider = ''\n")]
    [InlineData("model_provider = ' \t '\n")]
    public void MissingOrBuiltinProviderBecomesRetryProxy(string text)
    {
        var output = _editor.Write(text, Request(models: new() { Model = "gpt-test", ContextWindow = 1_000_000, AutoCompactTokenLimit = 900_000 }));
        var profile = _editor.Read(output);
        Assert.Equal("retry_proxy", profile.ProviderName);
        Assert.Equal("http://127.0.0.1:28080/v1", profile.BaseUrl);
        Assert.Equal(LocalToken, profile.ApiKey);
        Assert.Equal("responses", profile.WireApi);
        Assert.Equal("gpt-test", profile.Models.Model);
        Assert.Equal(1_000_000L, profile.Models.ContextWindow);
        Assert.Equal(900_000L, profile.Models.AutoCompactTokenLimit);
        if (text.Contains("#")) Assert.Contains(text.TrimEnd(), output);
        if (text.Contains("[model_providers.openai]")) Assert.Contains("[model_providers.openai]\nname = 'OpenAI'\n", output);
    }

    [Theory]
    [InlineData("model_provider = 'relay'", "\n")]
    [InlineData("model_provider = 'relay' # 尾注释", "\n")]
    [InlineData("model_provider = 'relay'\n[model_providers.relay]", "\n")]
    [InlineData("model_provider = 'relay'\n[model_providers.relay] # 表头尾注释", "\n")]
    [InlineData("model_provider = 'relay'\n[model_providers.relay]\nname = 'keep' # 行尾", "\n")]
    [InlineData("model_provider = 'relay'\r\n[model_providers.relay]\r\nname = 'keep' # 行尾", "\r\n")]
    [InlineData("model_provider = 'relay'\n[model_providers.relay.http_headers]\nx-project = 'keep'", "\n")]
    public void MissingFieldsAndNoFinalNewlineRemainValid(string text, string newline)
    {
        var output = _editor.Write(text, Request(models: new() { Model = "added" }));
        var profile = _editor.Read(output);
        Assert.Equal("relay", profile.ProviderName);
        Assert.Equal("added", profile.Models.Model);
        Assert.Equal(LocalToken, profile.ApiKey);
        Assert.Equal("responses", profile.WireApi);
        Assert.Contains($"base_url = \"http://127.0.0.1:28080/v1\"{newline}", output);
        foreach (var comment in new[] { "# 尾注释", "# 表头尾注释", "# 行尾" })
            if (text.Contains(comment)) Assert.Contains(comment + newline, output);
        if (newline == "\r\n") Assert.DoesNotContain("\n", output.Replace("\r\n", ""));
    }

    [Theory]
    [InlineData("https://upstream.example", "https://upstream.example/v1")]
    [InlineData("https://upstream.example/", "https://upstream.example/v1")]
    [InlineData(" https://upstream.example/v1/ ", "https://upstream.example/v1")]
    [InlineData("https://upstream.example/codex/", "https://upstream.example/codex")]
    public void DirectConnectionUsesSharedAddressRules(string address, string expected)
    {
        var output = _editor.Write(Existing, Request(proxy: false, address: address));
        var profile = _editor.Read(output);
        Assert.Equal(expected, profile.BaseUrl);
        Assert.Equal(UpstreamKey, profile.ApiKey);
        Assert.Equal("relay", profile.ProviderName);
        Assert.Equal("responses", profile.WireApi);
        Assert.DoesNotContain(LocalToken, output);
    }

    [Fact]
    public void DirectConnectionAndProxyRoundTripOnlyChangeResponsibleFields()
    {
        var taken = _editor.Write(Existing, Request());
        var direct = _editor.Write(taken, Request(proxy: false, address: "https://new.example/codex"));
        Assert.Equal("https://new.example/codex", _editor.Read(direct).BaseUrl);
        Assert.Equal(UpstreamKey, _editor.Read(direct).ApiKey);
        Assert.Equal(taken, _editor.Write(direct, Request()));
    }

    [Fact]
    public void ModelsOnlyUsesEffectiveOverrideAndDoesNotTouchProviderOrCredentials()
    {
        const string text = """
            model = 'previous'
            model_context_window = 123_000
            model_auto_compact_token_limit = 100_000
            model_provider = 'relay'
            model_providers = { relay = { base_url = 'https://keep.example', experimental_bearer_token = 'keep-key', env_key = 'ENV_NAME' } }
            """;
        var output = _editor.Write(text, Request(modelsOnly: true, models: new() { Model = "provider-model" },
            modelOverride: new() { Model = "key-model" }, key: "", token: "", address: "invalid", port: -1));
        Assert.Equal(text.Replace("'previous'", "\"key-model\""), output);
        Assert.Equal("keep-key", _editor.Read(output).ApiKey);
        Assert.Equal(123_000L, _editor.Read(output).Models.ContextWindow);
        Assert.Equal(100_000L, _editor.Read(output).Models.AutoCompactTokenLimit);
    }

    [Fact]
    public void EmptyModelAndNumbersAreNoOpsAndDoNotCreateProvider()
    {
        const string text = "# 模型同步\nmodel = 'keep'\n[unrelated]\nkeep = 0x10";
        Assert.Equal(text, _editor.Write(text, Request(modelsOnly: true, models: new() { Model = " \t " })));
        Assert.Equal("", _editor.Write("", Request(modelsOnly: true)));
        var changed = _editor.Write(text, Request(modelsOnly: true, models: new() { ContextWindow = 100 }));
        Assert.Equal("keep", _editor.Read(changed).Models.Model);
        Assert.DoesNotContain("model_provider", changed);
        Assert.Contains("[unrelated]\nkeep = 0x10", changed);
    }

    [Fact]
    public void SingleNumericFieldPreservesTheOtherAndChecksMergedLimits()
    {
        const string text = "model_context_window = 200\nmodel_auto_compact_token_limit = 100\n";
        Assert.Equal("model_context_window = 300\nmodel_auto_compact_token_limit = 100\n",
            _editor.Write(text, Request(modelsOnly: true, models: new() { ContextWindow = 300 })));
        Assert.Equal("model_context_window = 200\nmodel_auto_compact_token_limit = 150\n",
            _editor.Write(text, Request(modelsOnly: true, models: new() { AutoCompactTokenLimit = 150 })));
        Assert.Throws<ClientConfigException>(() => _editor.Write(text, Request(modelsOnly: true, models: new() { ContextWindow = 50 })));
        Assert.Throws<ClientConfigException>(() => _editor.Write(text, Request(modelsOnly: true, models: new() { AutoCompactTokenLimit = 300 })));
    }

    [Theory]
    [InlineData(0L, null)]
    [InlineData(-1L, null)]
    [InlineData(null, 0L)]
    [InlineData(null, -1L)]
    [InlineData(100L, 101L)]
    public void InvalidNumericModelsAreRejected(long? window, long? limit)
    {
        Assert.Throws<ClientConfigException>(() => _editor.Write("", Request(modelsOnly: true,
            models: new() { ContextWindow = window, AutoCompactTokenLimit = limit })));
    }

    [Fact]
    public void LongIntegerAndEscapedStringsRoundTrip()
    {
        const string key = "key\"\\\n\t\u0001汉字";
        const string model = "模型\"\\\t测试";
        var output = _editor.Write("", Request(proxy: false, key: key,
            models: new() { Model = model, ContextWindow = long.MaxValue, AutoCompactTokenLimit = long.MaxValue }));
        var read = _editor.Read(output);
        Assert.Equal(key, read.ApiKey);
        Assert.Equal(model, read.Models.Model);
        Assert.Equal(long.MaxValue, read.Models.ContextWindow);
        Assert.Equal(long.MaxValue, read.Models.AutoCompactTokenLimit);
    }

    public static IEnumerable<object[]> UnsupportedProviderLayouts()
    {
        yield return ["model_providers = { relay = { base_url = 'https://old.example' } }"];
        yield return ["model_providers = { relay = { name = 'only-name' } }"];
        yield return ["model_providers.relay.base_url = 'https://old.example'"];
        yield return ["model_providers.relay = { base_url = 'https://old.example' }"];
        yield return ["[model_providers]\nrelay.base_url = 'https://old.example'"];
        yield return ["[model_providers]\nrelay = { experimental_bearer_token = 'old-key' }"];
        yield return ["[model_providers.relay]\nbase_url.value = 'https://old.example'"];
        yield return ["[model_providers.relay]\nbase_url = { value = 'https://old.example' }"];
        yield return ["[[model_providers.relay]]\nbase_url = 'https://old.example'"];
        yield return ["[model_providers.relay.base_url]\nvalue = 'https://old.example'"];
        yield return ["[[model_providers]]\nrelay = { base_url = 'https://old.example' }"];
    }

    [Theory]
    [MemberData(nameof(UnsupportedProviderLayouts))]
    public void InlineOrDottedTargetsAndIncompatibleTablesAreRejected(string body)
    {
        var text = "model_provider = 'relay'\n" + body;
        var error = Assert.Throws<ClientConfigException>(() => _editor.Write(text, Request()));
        Assert.Contains("内联表", error.Message);
        Assert.Contains("点号键", error.Message);
        Assert.DoesNotContain("old-key", error.ToString());
        Assert.DoesNotContain("https://old.example", error.ToString());
    }

    [Theory]
    [InlineData("model = { name = 'old' }")]
    [InlineData("model.name = 'old'")]
    [InlineData("[model]\nname = 'old'")]
    public void ModelsOnlyRejectsUnsupportedTargetShape(string text)
    {
        Assert.Throws<ClientConfigException>(() => _editor.Write(text, Request(modelsOnly: true, models: new() { Model = "new" })));
    }

    [Fact]
    public void ReadCanInspectInlineAndDottedConfigsThatWriteRejects()
    {
        const string inline = "model_provider = 'relay'\nmodel_providers = { relay = { base_url = 'https://inline.example', experimental_bearer_token = 'inline-key' } }";
        const string dotted = "model_provider = 'relay'\nmodel_providers.relay.base_url = 'https://dotted.example'\nmodel_providers.relay.experimental_bearer_token = 'dotted-key'";
        Assert.Equal("inline-key", _editor.Read(inline).ApiKey);
        Assert.Equal("https://inline.example", _editor.Read(inline).BaseUrl);
        Assert.Equal("dotted-key", _editor.Read(dotted).ApiKey);
        Assert.Equal("https://dotted.example", _editor.Read(dotted).BaseUrl);
    }

    [Theory]
    [InlineData("env_key = 'SECRET_ENV'")]
    [InlineData("env_key = ''")]
    [InlineData("api_key = 'hidden-secret'")]
    [InlineData("requires_openai_auth = true")]
    [InlineData("requires_openai_auth = 'hidden-secret'")]
    [InlineData("http_headers = { Authorization = 'hidden-secret' }")]
    [InlineData("env_http_headers = { authorization = 'SECRET_ENV' }")]
    [InlineData("[model_providers.relay.http_headers]\nx-api-key = 'hidden-secret'")]
    [InlineData("auth = { command = 'hidden-secret' }")]
    [InlineData("aws = { profile = 'hidden-secret' }")]
    [InlineData("gateway_oauth = { issuer = 'hidden-secret' }")]
    public void ConflictingAuthenticationIsReportedAndRefusesProxyAndDirectWrites(string conflict)
    {
        var text = Existing + conflict;
        var localText = text.Replace("https://old.example/v1", "http://127.0.0.1:28080/v1").Replace("old-key", LocalToken);
        var profile = _editor.Read(localText);
        Assert.True(profile.HasConflictingSettings);
        Assert.Equal("http://127.0.0.1:28080/v1", profile.BaseUrl);
        Assert.Equal(LocalToken, profile.ApiKey);
        Assert.DoesNotContain("hidden-secret", profile.ToString());
        Assert.DoesNotContain(LocalToken, profile.ToString());
        foreach (var proxy in new[] { true, false })
        {
            var error = Assert.Throws<ClientConfigException>(() => _editor.Write(text, Request(proxy: proxy)));
            Assert.Contains("冲突", error.Message);
            Assert.DoesNotContain("hidden-secret", error.ToString());
            Assert.DoesNotContain("SECRET_ENV", error.ToString());
            Assert.Null(error.InnerException);
        }
    }

    [Fact]
    public void InactiveProviderAuthAndSafeHeadersArePreserved()
    {
        var text = Existing + "requires_openai_auth = false\nhttp_headers = { x-project = 'value' }\n"
            + "[model_providers.other]\nenv_key = 'OTHER_ENV'\nrequires_openai_auth = true\n";
        var output = _editor.Write(text, Request());
        Assert.Equal(text.Replace("\"https://old.example/v1\"", "\"http://127.0.0.1:28080/v1\"")
            .Replace("\"old-key\"", $"\"{LocalToken}\""), output);
        Assert.False(_editor.Read(text).HasConflictingSettings);
        Assert.False(_editor.Read(output).HasConflictingSettings);
    }

    [Theory]
    [InlineData("model_provider = 'different'")]
    [InlineData("model = 'profile-model'")]
    [InlineData("model_context_window = 123000")]
    [InlineData("model_auto_compact_token_limit = 100000")]
    public void ReadReportsEveryOwnedFieldOverriddenByTheActiveProfile(string entry)
    {
        var taken = _editor.Write(Existing, Request());
        var profile = _editor.Read("profile = 'active'\n" + taken + "[profiles.active]\n" + entry);
        Assert.Equal("http://127.0.0.1:28080/v1", profile.BaseUrl);
        Assert.Equal(LocalToken, profile.ApiKey);
        Assert.True(profile.HasConflictingSettings);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[profiles.inactive]\nmodel_provider = 'different'\nmodel = 'ignored'")]
    [InlineData("[profiles.active]\nmodel_reasoning_effort = 'high'\n[profiles.inactive]\nmodel = 'ignored'")]
    public void ReadDoesNotReportSafeOrInactiveProfileSettings(string profiles)
    {
        var taken = _editor.Write(Existing, Request());
        var selection = profiles.Contains("[profiles.active]", StringComparison.Ordinal) ? "profile = 'active'\n" : "";
        Assert.False(_editor.Read(selection + taken + profiles).HasConflictingSettings);
    }

    [Theory]
    [InlineData("''")]
    [InlineData("' \t '")]
    public void ExplicitBlankProviderUsesTheBuiltinProviderForReading(string selected)
    {
        var text = $"model_provider = {selected}\n[model_providers.openai]\nbase_url = 'https://official.example/v1'\nexperimental_bearer_token = 'builtin-key'\n";
        var profile = _editor.Read(text);
        Assert.Equal("openai", profile.ProviderName);
        Assert.Equal("https://official.example/v1", profile.BaseUrl);
        Assert.Equal("builtin-key", profile.ApiKey);
        Assert.False(profile.HasConflictingSettings);
        Assert.True(_editor.Read(text + "requires_openai_auth = true\n").HasConflictingSettings);
    }

    [Fact]
    public void ActiveProfileCannotSilentlyOverrideTargetsButInactiveProfilesAreKept()
    {
        var text = "profile = 'active'\n" + Existing
            + "[profiles.active]\nmodel_provider = 'other'\nmodel = 'profile-model'\n";
        Assert.Throws<ClientConfigException>(() => _editor.Write(text, Request()));
        Assert.Throws<ClientConfigException>(() => _editor.Write(text, Request(modelsOnly: true, models: new() { Model = "new-model" })));
        Assert.Equal(text, _editor.Write(text, Request(modelsOnly: true)));
        Assert.Contains("[profiles.active]\nmodel_provider = 'other'\nmodel = 'profile-model'\n",
            _editor.Write(text.Replace("profile = 'active'\n", ""), Request()));
    }

    [Fact]
    public void UnrelatedComplexSemanticsAndExactTextArePreserved()
    {
        const string unrelated = """
            # 各种合法类型
            [other]
            number = 0xDE_AD_BE_EF
            minimum = -9223372036854775808
            maximum = 0x7fffffffffffffff
            binary = 0b1010
            octal = 0o777
            fractional = +1.2300e+04
            not_a_number = nan
            infinity = -inf
            date = 1979-05-27
            time = 07:32:00.999999
            offset = 1979-05-27T07:32:00.123456-07:00
            local = 1979-05-27T07:32:00
            mixed = [1, 'two', true, { nested = [1.0, 2.0] }]
            multiline = '''first
            second'''
            dotted.non_target = { 'quoted.key' = '值' }
            [[other.servers]]
            name = 'one'
            [[other.servers]]
            name = 'two'
            """;
        var text = Existing + unrelated;
        var output = _editor.Write(text, Request());
        Assert.EndsWith(unrelated, output);
        var before = Toml.ToModel(text);
        var after = Toml.ToModel(output);
        Assert.True(CodexConfigEditor.SameSemantics(before["other"], after["other"]));
    }

    [Theory]
    [InlineData("[other]\nvalue = 1", "[other]\nvalue = 2")]
    [InlineData("[other]\nvalue = 1", "[other]\nvalue = 1.0")]
    [InlineData("[other]\nvalue = [1,2]", "[other]\nvalue = [2,1]")]
    [InlineData("[other]\nvalue = { a = 1 }", "[other]\nvalue = { b = 1 }")]
    [InlineData("[other]\nvalue = 1", "[other]\nvalue = 1\nextra = true")]
    [InlineData("[[other]]\nname = 'a'\n[[other]]\nname = 'b'", "[[other]]\nname = 'b'\n[[other]]\nname = 'a'")]
    [InlineData("[other]\ntime = 1979-05-27", "[other]\ntime = 1979-05-28")]
    public void SemanticVerificationDetectsNonTargetChanges(string left, string right)
    {
        Assert.False(CodexConfigEditor.SameSemantics(Toml.ToModel(left), Toml.ToModel(right)));
    }

    [Fact]
    public void SemanticVerificationIgnoresOnlyRepresentationNotValues()
    {
        Assert.True(CodexConfigEditor.SameSemantics(Toml.ToModel("a = 0x10\nb = 'x'\nc = nan"),
            Toml.ToModel("# 注释\nc = nan\nb = \"x\"\na = 16")));
    }

    [Theory]
    [InlineData("model = \"secret-unclosed")]
    [InlineData("secret-sensitive-name = [")]
    [InlineData("model = 'one'\nmodel = 'secret-duplicate'")]
    [InlineData("model_context_window = 9223372036854775808 # secret-overflow")]
    [InlineData("unrelated = -9223372036854775809 # secret-overflow")]
    [InlineData("unrelated = 0x8000000000000000 # secret-overflow")]
    [InlineData("unrelated = 0o1000000000000000000000 # secret-overflow")]
    [InlineData("unrelated = 0b1000000000000000000000000000000000000000000000000000000000000000 # secret-overflow")]
    public void MalformedTomlErrorsNeverExposeInput(string text)
    {
        foreach (Action action in new Action[] { () => _editor.Read(text), () => _editor.Write(text, Request()) })
        {
            var error = Assert.Throws<ClientConfigException>(action);
            Assert.DoesNotContain("secret", error.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(UpstreamKey, error.ToString());
            Assert.DoesNotContain(LocalToken, error.ToString());
            Assert.Null(error.InnerException);
        }
    }

    [Theory]
    [InlineData("{\"OPENAI_API_KEY\":\"secret-unclosed")]
    [InlineData("{\"OPENAI_API_KEY\":42,\"secret\":true}")]
    [InlineData("[\"secret\"]")]
    public void MalformedAuthErrorsNeverExposeInput(string auth)
    {
        var error = Assert.Throws<ClientConfigException>(() => _editor.Read("", auth));
        Assert.Contains("auth.json", error.Message);
        Assert.DoesNotContain("secret", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("https://secret@example.com")]
    [InlineData("https://example.com/?key=secret")]
    [InlineData("https://example.com/#secret")]
    [InlineData("not-a-url-secret")]
    public void InvalidAddressErrorsNeverExposeInput(string address)
    {
        var error = Assert.Throws<ClientConfigException>(() => _editor.Write(Existing, Request(proxy: false, address: address)));
        Assert.DoesNotContain("secret", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void InvalidPortEmptyCredentialsOrWrongClientAreRejected()
    {
        foreach (var request in new[]
        {
            Request(port: 0), Request(port: 65536), Request(key: ""), Request(token: " "),
            Request(proxy: false, key: ""), Request(clientType: ClientType.Claude),
        })
            Assert.Throws<ClientConfigException>(() => _editor.Write(Existing, request));
    }

    [Theory]
    [InlineData("model_provider = 42")]
    [InlineData("model = ['secret']")]
    [InlineData("model_context_window = 'secret'")]
    [InlineData("model_auto_compact_token_limit = true")]
    [InlineData("model_provider = 'relay'\n[model_providers.relay]\nexperimental_bearer_token = ['secret']")]
    public void WrongFieldTypesFailWithoutExposingValues(string text)
    {
        var error = Assert.Throws<ClientConfigException>(() => _editor.Read(text));
        Assert.DoesNotContain("secret", error.ToString());
        Assert.Null(error.InnerException);
    }
}
