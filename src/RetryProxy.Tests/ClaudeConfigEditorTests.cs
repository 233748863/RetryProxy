using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RetryProxy.Core.Cli;
using RetryProxy.Core.Client;
using RetryProxy.Core.Config;
using RetryProxy.Core.Internal;
using RetryProxy.Core.Proxy;
using Xunit;

namespace RetryProxy.Tests;

public sealed class ClaudeConfigEditorTests
{
    private readonly IClientConfigEditor _editor = new ClaudeConfigEditor();

    // 只使用内存里的虚构配置，不读取真实客户端配置，也不执行任何 helper 或 hook。
    private const string Existing = """
        {
          "$schema": "https://example.invalid/settings.schema.json",
          "env": {
            "ANTHROPIC_BASE_URL": "https://old.example/api",
            "ANTHROPIC_AUTH_TOKEN": "old-auth-secret",
            "ANTHROPIC_API_KEY": "old-api-secret",
            "ANTHROPIC_MODEL": "old-model",
            "CLAUDE_CODE_SUBAGENT_MODEL": "claude-sonnet-4-6[1M]",
            "OTHER_TOKEN": "keep-this-secret",
            "ANTHROPIC_MODEL_SUFFIX": "unrelated",
            "API_TIMEOUT_MS": "600000",
            "unrelated": { "array": [1, false, null, "保留"], "model": "nested" }
          },
          "apiKeyHelper": "never-run-this-secret-command",
          "permissions": { "allow": ["Read", "Bash(dotnet test *)"], "deny": ["Write"] },
          "hooks": { "Stop": [{ "hooks": [{ "type": "command", "command": "never-run-hook" }] }] },
          "model": "user-root-model[1M]",
          "custom": { "model": "nested-model", "amount": 1.2300e+04 },
          "enabled": true
        }
        """;

    private static ClientConfigRequest Request(
        ProviderModels? models = null,
        bool useProxy = true,
        bool modelsOnly = false,
        ClaudeAuthMode authMode = ClaudeAuthMode.Bearer,
        KeyModelOverride? modelOverride = null,
        int port = 28081) => new()
    {
        ListenPort = port,
        UseProxy = useProxy,
        ModelsOnly = modelsOnly,
        Channel = new ChannelSnapshot
        {
            ClientType = ClientType.Claude,
            ApiKey = "real-key-secret",
            LocalToken = "local-token-secret",
            UpstreamBaseUrl = "https://new.example/api",
            AuthMode = authMode,
            Models = models ?? new ProviderModels { Model = "vendor-main", Context1M = true },
            ModelOverride = modelOverride,
        },
    };

    private static JsonObject Parse(string text) => JsonNode.Parse(text)!.AsObject();
    private static JsonObject Env(string text) => Parse(text)["env"]!.AsObject();
    private static string Text(JsonObject obj, string key) => obj[key]!.GetValue<string>();

    [Fact]
    public void ReadExtractsCredentialsModelsAndHelperWithoutExecutingAnything()
    {
        const string text = """
            {"env":{
              "ANTHROPIC_BASE_URL":"https://fixture.example",
              "ANTHROPIC_AUTH_TOKEN":"bearer-secret",
              "ANTHROPIC_API_KEY":"api-secret",
              "ANTHROPIC_MODEL":"vendor-main[1M]",
              "ANTHROPIC_MODEL_NAME":"display-is-not-the-model",
              "ANTHROPIC_DEFAULT_OPUS_MODEL":"vendor-opus[1m]",
              "ANTHROPIC_DEFAULT_SONNET_MODEL":"vendor-sonnet",
              "ANTHROPIC_DEFAULT_HAIKU_MODEL":"vendor-haiku[1M]",
              "ANTHROPIC_DEFAULT_FABLE_MODEL":"vendor-fable",
              "ANTHROPIC_DEFAULT_FABLE_MODEL_NAME":"display-fable"},
              "apiKeyHelper":"never-run",
              "model":"root-must-not-be-imported"}
            """;
        var profile = _editor.Read(text, "invalid auth text must be ignored");
        Assert.Equal(ClientType.Claude, _editor.ClientType);
        Assert.Equal(ClientType.Claude, profile.ClientType);
        Assert.Equal("https://fixture.example", profile.BaseUrl);
        Assert.Equal("bearer-secret", profile.ApiKey);
        Assert.Equal(ClaudeAuthMode.Bearer, profile.AuthMode);
        Assert.True(profile.HasApiKeyHelper);
        Assert.Equal("vendor-main", profile.Models.Model);
        Assert.True(profile.Models.Context1M);
        Assert.Equal(new RoleModel { Model = "vendor-opus", Context1M = true }, profile.Models.Opus);
        Assert.Equal(new RoleModel { Model = "vendor-sonnet" }, profile.Models.Sonnet);
        Assert.Equal(new RoleModel { Model = "vendor-haiku", Context1M = true }, profile.Models.Haiku);
        Assert.Equal(new RoleModel { Model = "vendor-fable" }, profile.Models.Fable);
        Assert.DoesNotContain("secret", profile.ToString());
    }

    [Theory]
    [InlineData("{}", "", false)]
    [InlineData("{\"ANTHROPIC_API_KEY\":\"api-secret\"}", "api-secret", true)]
    [InlineData("{\"ANTHROPIC_AUTH_TOKEN\":\"\",\"ANTHROPIC_API_KEY\":\"api-secret\"}", "api-secret", true)]
    [InlineData("{\"ANTHROPIC_AUTH_TOKEN\":\"bearer-secret\"}", "bearer-secret", false)]
    public void ReadUsesApiKeyWhenAuthTokenIsAbsentOrEmpty(string env, string expectedKey, bool apiKey)
    {
        var profile = _editor.Read("{\"env\":" + env + "}");
        Assert.Equal(expectedKey, profile.ApiKey);
        if (expectedKey.Length > 0)
        {
            Assert.Equal(apiKey ? ClaudeAuthMode.ApiKey : ClaudeAuthMode.Bearer, profile.AuthMode);
        }
        Assert.False(profile.HasApiKeyHelper);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \r\n\t")]
    [InlineData("{}")]
    [InlineData("{\"env\":{}}")]
    public void EmptyConfigurationCanBeReadAndTakenOver(string text)
    {
        var profile = _editor.Read(text);
        Assert.Equal(string.Empty, profile.BaseUrl);
        Assert.Equal(string.Empty, profile.ApiKey);
        Assert.Equal(string.Empty, profile.Models.Model);
        Assert.False(profile.Models.Context1M);
        var env = Env(_editor.Write(text, Request()));
        Assert.Equal("http://127.0.0.1:28081", Text(env, "ANTHROPIC_BASE_URL"));
        Assert.Equal("local-token-secret", Text(env, "ANTHROPIC_AUTH_TOKEN"));
        Assert.Equal("retry-proxy-main[1M]", Text(env, "ANTHROPIC_MODEL"));
    }

    [Fact]
    public void TakeoverMergesOnlyOwnedFieldsAndPreservesOtherJsonSemantics()
    {
        var result = Parse(_editor.Write(Existing, Request(authMode: ClaudeAuthMode.ApiKey)));
        var env = result["env"]!.AsObject();
        Assert.Equal("http://127.0.0.1:28081", Text(env, "ANTHROPIC_BASE_URL"));
        Assert.Equal("local-token-secret", Text(env, "ANTHROPIC_AUTH_TOKEN"));
        Assert.False(env.ContainsKey("ANTHROPIC_API_KEY"));
        Assert.False(result.ContainsKey("apiKeyHelper"));
        var original = Parse(Existing);
        foreach (var key in new[] { "$schema", "permissions", "hooks", "model", "custom", "enabled" })
        {
            Assert.True(JsonNode.DeepEquals(original[key], result[key]), key);
        }
        foreach (var key in new[] { "OTHER_TOKEN", "ANTHROPIC_MODEL_SUFFIX", "API_TIMEOUT_MS", "unrelated" })
        {
            Assert.True(JsonNode.DeepEquals(original["env"]![key], env[key]), key);
        }
        Assert.DoesNotContain("real-key-secret", result.ToJsonString());
    }

    [Theory]
    [InlineData("claude-opus-other")]
    [InlineData("prefix-SONNET-custom")]
    [InlineData("vendor-haiku")]
    [InlineData("vendor-Fable")]
    [InlineData("unknown-vendor-model")]
    public void MainModelUsesAnIndependentMarkerRegardlessOfItsRoleKeyword(string model)
    {
        var env = Env(_editor.Write("{}", Request(new ProviderModels { Model = model, Context1M = true })));
        Assert.Equal("retry-proxy-main[1M]", Text(env, "ANTHROPIC_MODEL"));
        Assert.Equal(model, Text(env, "ANTHROPIC_MODEL_NAME"));
    }

    [Fact]
    public void FourRolesAndMainInheritTheSameTargetAndContextAsTheProxy()
    {
        var models = new ProviderModels
        {
            Model = "vendor-sonnet-main",
            Context1M = true,
            Opus = new RoleModel { Model = "opus-private", Context1M = true },
            Sonnet = new RoleModel { Model = "sonnet-private", Context1M = false },
            Haiku = new RoleModel { Context1M = false },
            Fable = new RoleModel { Model = "fable-private", Context1M = true },
        };
        var request = Request(models);
        var env = Env(_editor.Write(Existing, request));
        Assert.Equal("claude-opus-5[1M]", Text(env, "ANTHROPIC_DEFAULT_OPUS_MODEL"));
        Assert.Equal("claude-sonnet-5", Text(env, "ANTHROPIC_DEFAULT_SONNET_MODEL"));
        Assert.Equal("claude-haiku-4-5[1M]", Text(env, "ANTHROPIC_DEFAULT_HAIKU_MODEL"));
        Assert.Equal("claude-fable-5[1M]", Text(env, "ANTHROPIC_DEFAULT_FABLE_MODEL"));
        Assert.Equal("retry-proxy-main[1M]", Text(env, "ANTHROPIC_MODEL"));
        Assert.Equal("vendor-sonnet-main", Text(env, "ANTHROPIC_MODEL_NAME"));
        Assert.Equal("claude-sonnet-5", Text(env, "CLAUDE_CODE_SUBAGENT_MODEL"));
        AssertModelsMatchRewriter(env, request);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KeyOverrideReplacesMainAndInheritedRolesButNotIndependentRoles(bool useProxy)
    {
        var models = new ProviderModels
        {
            Model = "old-main",
            Context1M = true,
            Opus = new RoleModel { Model = "independent-opus", Context1M = true },
            Fable = new RoleModel { Model = "independent-fable", Context1M = false },
        };
        var request = Request(models, useProxy: useProxy,
            modelOverride: new KeyModelOverride { Model = "custom-haiku-main", Context1M = false });
        var env = Env(_editor.Write("{}", request));
        Assert.Equal(useProxy ? "retry-proxy-main" : "custom-haiku-main", Text(env, "ANTHROPIC_MODEL"));
        Assert.Equal(useProxy ? "claude-opus-5[1M]" : "independent-opus[1M]", Text(env, "ANTHROPIC_DEFAULT_OPUS_MODEL"));
        Assert.Equal(useProxy ? "claude-sonnet-5" : "custom-haiku-main", Text(env, "ANTHROPIC_DEFAULT_SONNET_MODEL"));
        Assert.Equal("custom-haiku-main", Text(env, "ANTHROPIC_DEFAULT_SONNET_MODEL_NAME"));
        Assert.Equal("independent-fable", Text(env, "ANTHROPIC_DEFAULT_FABLE_MODEL_NAME"));
        Assert.Equal("old-main", models.Model);
        Assert.True(models.Context1M);
        if (useProxy)
        {
            AssertModelsMatchRewriter(env, request);
        }
    }

    [Fact]
    public void EmptyKeyOverrideDoesNotReplaceTheMainModelOrItsContext()
    {
        var env = Env(_editor.Write("{}", Request(modelOverride: new KeyModelOverride { Model = "", Context1M = false })));
        Assert.Equal("retry-proxy-main[1M]", Text(env, "ANTHROPIC_MODEL"));
        Assert.Equal("vendor-main", Text(env, "ANTHROPIC_MODEL_NAME"));
    }

    [Theory]
    [InlineData(ClaudeAuthMode.Bearer, "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_API_KEY")]
    [InlineData(ClaudeAuthMode.ApiKey, "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN")]
    public void DirectConnectionUsesTheRealAddressKeyAndModelsAndRemovesConflictingAuth(
        ClaudeAuthMode mode, string present, string absent)
    {
        var result = Parse(_editor.Write(Existing, Request(useProxy: false, authMode: mode)));
        var env = result["env"]!.AsObject();
        Assert.Equal("https://new.example/api", Text(env, "ANTHROPIC_BASE_URL"));
        Assert.Equal("real-key-secret", Text(env, present));
        Assert.False(env.ContainsKey(absent));
        Assert.False(result.ContainsKey("apiKeyHelper"));
        Assert.Equal("vendor-main[1M]", Text(env, "ANTHROPIC_MODEL"));
        Assert.Equal("vendor-main[1M]", Text(env, "ANTHROPIC_DEFAULT_FABLE_MODEL"));
        Assert.Equal("vendor-main[1M]", Text(env, "CLAUDE_CODE_SUBAGENT_MODEL"));
        Assert.Equal("vendor-main", Text(env, "ANTHROPIC_MODEL_NAME"));
        Assert.Equal("user-root-model[1M]", Text(result, "model"));
        var switched = Env(_editor.Write(result.ToJsonString(), Request(useProxy: false,
            authMode: mode == ClaudeAuthMode.Bearer ? ClaudeAuthMode.ApiKey : ClaudeAuthMode.Bearer)));
        Assert.False(switched.ContainsKey(present));
        Assert.Equal("real-key-secret", Text(switched, absent));
    }

    [Theory]
    [InlineData(true, ClaudeAuthMode.Bearer)]
    [InlineData(true, ClaudeAuthMode.ApiKey)]
    [InlineData(false, ClaudeAuthMode.Bearer)]
    [InlineData(false, ClaudeAuthMode.ApiKey)]
    public void ModelsOnlyPreservesAddressBothCredentialsAndHelper(bool useProxy, ClaudeAuthMode authMode)
    {
        var output = Parse(_editor.Write(Existing, Request(useProxy: useProxy, modelsOnly: true, authMode: authMode)));
        var original = Parse(Existing);
        Assert.True(JsonNode.DeepEquals(original["apiKeyHelper"], output["apiKeyHelper"]));
        foreach (var key in new[] { "ANTHROPIC_BASE_URL", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_API_KEY", "OTHER_TOKEN" })
        {
            Assert.True(JsonNode.DeepEquals(original["env"]![key], output["env"]![key]), key);
        }
        Assert.Equal(useProxy ? "retry-proxy-main[1M]" : "vendor-main[1M]", output["env"]!["ANTHROPIC_MODEL"]!.GetValue<string>());
        Assert.Equal("user-root-model[1M]", Text(output, "model"));
    }

    [Fact]
    public void ModelsOnlyDoesNotRequireConnectionSettingsOrCreateThem()
    {
        var request = new ClientConfigRequest
        {
            UseProxy = true,
            ModelsOnly = true,
            Channel = new ChannelSnapshot { ClientType = ClientType.Claude, Models = new ProviderModels { Model = "custom-main" } },
        };
        var env = Env(_editor.Write("{}", request));
        Assert.False(env.ContainsKey("ANTHROPIC_BASE_URL"));
        Assert.False(env.ContainsKey("ANTHROPIC_AUTH_TOKEN"));
        Assert.False(env.ContainsKey("ANTHROPIC_API_KEY"));
        Assert.Equal("retry-proxy-main", Text(env, "ANTHROPIC_MODEL"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EmptyModelsPreserveEveryExistingModelAndDoNotInventOfficialDefaults(bool useProxy)
    {
        var request = Request(new ProviderModels(), useProxy: useProxy, modelsOnly: true);
        var output = _editor.Write(Existing, request);
        Assert.True(JsonNode.DeepEquals(Parse(Existing), Parse(output)));
        Assert.True(JsonNode.DeepEquals(Parse("{}"), Parse(_editor.Write("{}", request))));
        var takenOver = Env(_editor.Write("{}", Request(new ProviderModels(), useProxy: useProxy)));
        Assert.DoesNotContain(takenOver, pair => pair.Key.Contains("MODEL", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("old-custom-model", "old-custom-model")]
    [InlineData("claude-opus-4-6", "claude-opus-4-6")]
    [InlineData("claude-sonnet-4-6", "claude-sonnet-4-6")]
    public void EmptyMainStillAllowsIndependentRolesWithoutCreatingUnmappedModels(string existingMain, string expectedMain)
    {
        var source = new JsonObject
        {
            ["env"] = new JsonObject
            {
                ["ANTHROPIC_MODEL"] = existingMain,
                ["ANTHROPIC_DEFAULT_OPUS_MODEL"] = "keep-opus-custom",
                ["CLAUDE_CODE_SUBAGENT_MODEL"] = "claude-sonnet-4-6",
            },
        };
        var request = Request(new ProviderModels { Sonnet = new RoleModel { Model = "sonnet-only-target", Context1M = true } });
        var env = Env(_editor.Write(source.ToJsonString(), request));
        Assert.Equal(expectedMain, Text(env, "ANTHROPIC_MODEL"));
        Assert.Equal("keep-opus-custom", Text(env, "ANTHROPIC_DEFAULT_OPUS_MODEL"));
        Assert.Equal("claude-sonnet-5[1M]", Text(env, "ANTHROPIC_DEFAULT_SONNET_MODEL"));
        Assert.Equal("sonnet-only-target", Text(env, "ANTHROPIC_DEFAULT_SONNET_MODEL_NAME"));
        Assert.Equal("claude-sonnet-5[1M]", Text(env, "CLAUDE_CODE_SUBAGENT_MODEL"));
        Assert.False(env.ContainsKey("ANTHROPIC_DEFAULT_HAIKU_MODEL"));
        Assert.False(env.ContainsKey("ANTHROPIC_DEFAULT_FABLE_MODEL"));
        Assert.NotNull(ModelRewriter.ClaudeTarget(request.Channel, Text(env, "ANTHROPIC_DEFAULT_SONNET_MODEL")));
    }

    [Theory]
    [InlineData("claude-opus-4-6", "claude-opus-5")]
    [InlineData("claude-sonnet-4-6", "claude-sonnet-5")]
    [InlineData("claude-haiku-4-5", "claude-haiku-4-5")]
    [InlineData("claude-fable-5", "claude-fable-5")]
    public void ExistingSubagentModelMapsByRoleAndMissingOneIsNotCreated(string existing, string official)
    {
        var env = Env(_editor.Write("{\"env\":{\"CLAUDE_CODE_SUBAGENT_MODEL\":\"" + existing + "\"}}", Request()));
        Assert.Equal(official + "[1M]", Text(env, "CLAUDE_CODE_SUBAGENT_MODEL"));
        Assert.False(env.ContainsKey("CLAUDE_CODE_SUBAGENT_MODEL_NAME"));
        Assert.False(Env(_editor.Write("{}", Request())).ContainsKey("CLAUDE_CODE_SUBAGENT_MODEL"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FourIndependentRolesCannotOverrideTheOrdinaryConversationMainModel(bool useProxy)
    {
        var request = Request(new ProviderModels
        {
            Model = "claude-opus-5-5",
            Context1M = true,
            Opus = new RoleModel { Model = "glm-5", Context1M = false },
            Sonnet = new RoleModel { Model = "vendor-sonnet", Context1M = false },
            Haiku = new RoleModel { Model = "vendor-haiku", Context1M = true },
            Fable = new RoleModel { Model = "vendor-fable", Context1M = false },
        }, useProxy: useProxy);
        var env = Env(_editor.Write("{}", request));
        Assert.Equal(useProxy ? "retry-proxy-main[1M]" : "claude-opus-5-5[1M]", Text(env, "ANTHROPIC_MODEL"));
        Assert.Equal("claude-opus-5-5", Text(env, "ANTHROPIC_MODEL_NAME"));
        Assert.Equal(useProxy ? "claude-opus-5" : "glm-5", Text(env, "ANTHROPIC_DEFAULT_OPUS_MODEL"));
        if (useProxy)
        {
            AssertModelsMatchRewriter(env, request);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnknownSubagentModelUsesTheIndependentMainModel(bool useProxy)
    {
        const string source = "{\"env\":{\"CLAUDE_CODE_SUBAGENT_MODEL\":\"unknown-custom-model\"}}";
        var request = Request(new ProviderModels
        {
            Model = "main-target",
            Context1M = true,
            Opus = new RoleModel { Model = "independent-opus" },
        }, useProxy: useProxy);
        var env = Env(_editor.Write(source, request));
        Assert.Equal(useProxy ? "retry-proxy-main[1M]" : "main-target[1M]", Text(env, "CLAUDE_CODE_SUBAGENT_MODEL"));
        var empty = Env(_editor.Write(source, Request(new ProviderModels(), useProxy: useProxy)));
        Assert.Equal("unknown-custom-model", Text(empty, "CLAUDE_CODE_SUBAGENT_MODEL"));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void SwitchingFromTakenOverToEmptyModelsRestoresPreviousRealNames(bool useProxy, bool modelsOnly)
    {
        var models = new ProviderModels
        {
            Model = "previous-main",
            Context1M = true,
            Opus = new RoleModel { Model = "previous-opus", Context1M = false },
            Sonnet = new RoleModel { Model = "previous-sonnet", Context1M = true },
            Haiku = new RoleModel { Model = "previous-haiku", Context1M = false },
            Fable = new RoleModel { Model = "previous-fable", Context1M = true },
        };
        var takenOver = _editor.Write("{\"env\":{\"CLAUDE_CODE_SUBAGENT_MODEL\":\"unknown\"}}", Request(models));
        var output = _editor.Write(takenOver, Request(new ProviderModels(), useProxy: useProxy, modelsOnly: modelsOnly));
        var env = Env(output);
        Assert.Equal(models, _editor.Read(output).Models);
        Assert.Equal("previous-main[1M]", Text(env, "CLAUDE_CODE_SUBAGENT_MODEL"));
        Assert.DoesNotContain("retry-proxy-main", output);
        if (modelsOnly)
        {
            Assert.Equal(Text(Env(takenOver), "ANTHROPIC_BASE_URL"), Text(env, "ANTHROPIC_BASE_URL"));
            Assert.Equal(Text(Env(takenOver), "ANTHROPIC_AUTH_TOKEN"), Text(env, "ANTHROPIC_AUTH_TOKEN"));
        }
        else
        {
            Assert.Equal(useProxy ? "local-token-secret" : "real-key-secret", Text(env, "ANTHROPIC_AUTH_TOKEN"));
        }
        Assert.Equal(output, _editor.Write(output, Request(new ProviderModels(), useProxy: useProxy, modelsOnly: modelsOnly)));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("[1M]", true)]
    public void EmptyModelRecoveryKeepsTheCurrentContextFlagAndSubagentRole(string suffix, bool context1M)
    {
        var source = new JsonObject
        {
            ["env"] = new JsonObject
            {
                ["ANTHROPIC_MODEL"] = "retry-proxy-main" + suffix,
                ["ANTHROPIC_MODEL_NAME"] = "previous-main[1M]",
                ["CLAUDE_CODE_SUBAGENT_MODEL"] = "claude-sonnet-5" + suffix,
                ["ANTHROPIC_DEFAULT_SONNET_MODEL_NAME"] = "previous-sonnet[1M]",
                ["ANTHROPIC_DEFAULT_OPUS_MODEL"] = "user-custom-opus",
                ["ANTHROPIC_DEFAULT_OPUS_MODEL_NAME"] = "must-not-replace-user-model",
            },
        };
        var output = _editor.Write(source.ToJsonString(), Request(new ProviderModels(), modelsOnly: true));
        Assert.Equal("previous-main" + suffix, Text(Env(output), "ANTHROPIC_MODEL"));
        Assert.Equal("previous-sonnet" + suffix, Text(Env(output), "CLAUDE_CODE_SUBAGENT_MODEL"));
        Assert.Equal("user-custom-opus", Text(Env(output), "ANTHROPIC_DEFAULT_OPUS_MODEL"));
        Assert.Equal(context1M, _editor.Read(output).Models.Context1M);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("retry-proxy-main[1M]")]
    public void InternalMarkersWithoutRecoverableNamesAreRemovedButOfficialModelsAreKept(string? name)
    {
        var env = new JsonObject
        {
            ["ANTHROPIC_MODEL"] = "retry-proxy-main[1M]",
            ["CLAUDE_CODE_SUBAGENT_MODEL"] = "retry-proxy-main",
            ["ANTHROPIC_DEFAULT_OPUS_MODEL"] = "claude-opus-5[1M]",
        };
        if (name is not null)
        {
            env["ANTHROPIC_MODEL_NAME"] = name;
        }
        var source = new JsonObject { ["env"] = env };
        foreach (var useProxy in new[] { true, false })
        {
            var output = Env(_editor.Write(source.ToJsonString(), Request(new ProviderModels(), useProxy: useProxy, modelsOnly: true)));
            Assert.False(output.ContainsKey("ANTHROPIC_MODEL"));
            Assert.False(output.ContainsKey("CLAUDE_CODE_SUBAGENT_MODEL"));
            Assert.Equal("claude-opus-5[1M]", Text(output, "ANTHROPIC_DEFAULT_OPUS_MODEL"));
        }
    }

    [Fact]
    public void RepeatedWritesAreIdempotentAndContextCanBeRemoved()
    {
        var first = _editor.Write(Existing, Request());
        Assert.Equal(first, _editor.Write(first, Request()));
        var off = Request(new ProviderModels { Model = "vendor-main", Context1M = false });
        var second = _editor.Write(first, off);
        Assert.Equal(second, _editor.Write(second, off));
        Assert.All(Env(second).Where(pair => pair.Key.EndsWith("MODEL", StringComparison.Ordinal)),
            pair => Assert.DoesNotContain("[1M]", pair.Value!.GetValue<string>()));
    }

    public static IEnumerable<object[]> UnsafeConfigurations()
    {
        foreach (var text in new[]
        {
            "{\"env\":{\"ANTHROPIC_AUTH_TOKEN\":\"redact-me-secret\"}, broken}",
            "{\"env\":{},}", "// redact-me-secret\n{}", "null", "[]", "42", "\"redact-me-secret\"",
            "{\"env\":{},\"env\":{}}",
            "{\"apiKeyHelper\":\"redact-me-secret\",\"apiKeyHelper\":\"other\"}",
            "{\"env\":{\"ANTHROPIC_AUTH_TOKEN\":\"redact-me-secret\",\"ANTHROPIC_AUTH_TOKEN\":\"other\"}}",
            "{\"env\":{\"ANTHROPIC_MODEL\":\"redact-me-secret\",\"ANTHROPIC_\\u004dODEL\":\"other\"}}",
            "{\"env\":{\"ANTHROPIC_MODEL_NAME\":\"redact-me-secret\",\"ANTHROPIC_MODEL_NAME\":\"other\"}}",
            "{\"env\":{\"ANTHROPIC_DEFAULT_FABLE_MODEL\":\"redact-me-secret\",\"ANTHROPIC_DEFAULT_FABLE_MODEL\":\"other\"}}",
            "{\"custom\":{\"redact-me-secret\":1,\"redact-me-secret\":2}}",
            "{\"env\":{\"ANTHROPIC_MODEL\":\"\\uD800\"}}",
        })
        {
            yield return new object[] { text };
        }
        foreach (var value in new[] { "null", "42", "[]", "true", "\"redact-me-secret\"" })
        {
            yield return new object[] { "{\"env\":" + value + "}" };
        }
        foreach (var key in new[]
        {
            "ANTHROPIC_BASE_URL", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_API_KEY", "ANTHROPIC_MODEL",
            "ANTHROPIC_MODEL_NAME", "ANTHROPIC_DEFAULT_OPUS_MODEL", "ANTHROPIC_DEFAULT_SONNET_MODEL",
            "ANTHROPIC_DEFAULT_HAIKU_MODEL", "ANTHROPIC_DEFAULT_FABLE_MODEL", "CLAUDE_CODE_SUBAGENT_MODEL",
            "ANTHROPIC_DEFAULT_OPUS_MODEL_NAME", "ANTHROPIC_DEFAULT_SONNET_MODEL_NAME",
            "ANTHROPIC_DEFAULT_HAIKU_MODEL_NAME", "ANTHROPIC_DEFAULT_FABLE_MODEL_NAME",
        })
        {
            yield return new object[] { "{\"env\":{\"" + key + "\":{\"secret\":\"redact-me-secret\"}}}" };
            yield return new object[] { "{\"env\":{\"" + key + "\":null}}" };
        }
        foreach (var value in new[] { "null", "42", "[]", "{}", "false" })
        {
            yield return new object[] { "{\"apiKeyHelper\":" + value + "}" };
        }
    }

    [Theory]
    [MemberData(nameof(UnsafeConfigurations))]
    public void InvalidAmbiguousOrWronglyTypedJsonProducesOnlySafeChineseErrors(string text)
    {
        AssertSafeError(() => _editor.Read(text));
        AssertSafeError(() => _editor.Write(text, Request()));
        AssertSafeError(() => _editor.Write(text, Request(modelsOnly: true)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void InvalidProxyPortIsRejectedWithASafeError(int port)
    {
        AssertSafeError(() => _editor.Write("{}", Request(port: port)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(65535)]
    public void ValidPortBoundariesAreAccepted(int port)
    {
        Assert.Equal("http://127.0.0.1:" + port, Text(Env(_editor.Write("{}", Request(port: port))), "ANTHROPIC_BASE_URL"));
    }

    [Fact]
    public void WrongClientAndMissingKeyAreRejected()
    {
        AssertSafeError(() => _editor.Write("{}", new ClientConfigRequest { Channel = new ChannelSnapshot { ClientType = ClientType.Codex } }));
        AssertSafeError(() => _editor.Write("{}", new ClientConfigRequest { Channel = new ChannelSnapshot { ClientType = ClientType.Claude } }));
    }

    private static void AssertSafeError(Action action)
    {
        var error = Assert.Throws<ClientConfigException>(action);
        Assert.Null(error.InnerException);
        Assert.Matches("[\\u4e00-\\u9fff]", error.Message);
        Assert.DoesNotContain("redact-me-secret", error.ToString());
        Assert.DoesNotContain("real-key-secret", error.ToString());
        Assert.DoesNotContain("local-token-secret", error.ToString());
    }

    private static void AssertModelsMatchRewriter(JsonObject env, ClientConfigRequest request)
    {
        foreach (var key in new[]
        {
            "ANTHROPIC_MODEL", "ANTHROPIC_DEFAULT_OPUS_MODEL", "ANTHROPIC_DEFAULT_SONNET_MODEL",
            "ANTHROPIC_DEFAULT_HAIKU_MODEL", "ANTHROPIC_DEFAULT_FABLE_MODEL",
        })
        {
            var value = Text(env, key);
            var context1M = value.EndsWith("[1M]", StringComparison.Ordinal);
            var model = context1M ? value[..^4] : value;
            var target = ModelRewriter.ClaudeTarget(request.Channel, model);
            Assert.NotNull(target);
            Assert.Equal(Text(env, key + "_NAME"), target.Value.Model);
            Assert.Equal(context1M, target.Value.Context1M);
            var headers = new HeaderList();
            headers.Append("content-type", "application/json");
            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { model }));
            var rewritten = ModelRewriter.Apply(ClientType.Claude, request.Channel, headers, body);
            using var parsed = JsonDocument.Parse(rewritten.Body);
            Assert.Equal(target.Value.Model, parsed.RootElement.GetProperty("model").GetString());
        }
    }
}
