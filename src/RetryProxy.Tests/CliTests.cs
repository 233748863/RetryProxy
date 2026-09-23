using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using RetryProxy.Core.Cli;
using RetryProxy.Core.KeepAlive;
using Xunit;

namespace RetryProxy.Tests;

[Collection("cli-environment")]
/// <summary>对应 keepalive_cli.rs 里的单元测试：凭据、CLI 覆盖参数、安全错误文案与 Codex 事件解析。</summary>
public class CliTests
{
    private static JsonNode Json(string text) => JsonNode.Parse(text)!;

    [Fact]
    public void CredentialsAreTrimmedValidatedAndNeverDebugPrinted()
    {
        var credential = CliCredential.Create("  sk-test-secret \n", "http://127.0.0.1:18081/");
        Assert.Equal("sk-test-secret", credential.ApiKey);
        Assert.Equal("http://127.0.0.1:18081", credential.BaseUrl);
        Assert.Null(credential.Model);
        Assert.DoesNotContain("sk-test-secret", credential.ToString());
        var selected = CliCredential.Create("sk-test-secret", "http://127.0.0.1:18081", "chosen-model");
        Assert.Equal("chosen-model", selected.Model);
        Assert.NotEqual(credential, selected);
        Assert.DoesNotContain("sk-test-secret", selected.ToString());
        Assert.Throws<CliException>(() => CliCredential.Create("   ", "http://127.0.0.1:18081"));
        Assert.Throws<CliException>(() => CliCredential.Create("sk-a b", "http://127.0.0.1:18081"));
        Assert.Throws<CliException>(() => CliCredential.Create("sk-a\tb", "http://127.0.0.1:18081"));
        Assert.Throws<CliException>(() => CliCredential.Create("sk-ok", " "));
    }

    [Fact]
    public void ClaudeSettingsOverrideUserEnvWithTheSuppliedKeyAndChannelAddress()
    {
        var credential = CliCredential.Create("sk-test-secret", "http://127.0.0.1:18081");
        var settings = Json(CliSession.ClaudeCredentialSettings(credential));
        Assert.True(settings["disableAllHooks"]!.GetValue<bool>());
        Assert.Equal("sk-test-secret", settings["env"]!["ANTHROPIC_AUTH_TOKEN"]!.GetValue<string>());
        Assert.Equal(string.Empty, settings["env"]!["ANTHROPIC_API_KEY"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:18081", settings["env"]!["ANTHROPIC_BASE_URL"]!.GetValue<string>());
    }

    [Fact]
    public void CodexOverridesSelectATemporaryProviderWithoutPuttingTheKeyInArguments()
    {
        var credential = CliCredential.Create("sk-test-secret", "http://127.0.0.1:18080", "chosen-model");
        var overrides = CliSession.CodexCredentialOverrides(credential);
        Assert.Contains("model_provider=\"retry_proxy_prepare\"", overrides);
        Assert.Contains("model_providers.retry_proxy_prepare.base_url=\"http://127.0.0.1:18080/v1\"", overrides);
        Assert.Contains("model_providers.retry_proxy_prepare.env_key=\"RETRY_PROXY_PREPARE_KEY\"", overrides);
        Assert.Contains("model=\"chosen-model\"", overrides);
        Assert.All(overrides, setting => Assert.DoesNotContain("sk-test-secret", setting));
        foreach (var setting in overrides)
        {
            // 每条都是 TOML 的 `键 = "字符串"`：键只含标识符与点，值是合法 JSON 字符串。
            var separator = setting.IndexOf('=');
            Assert.True(separator > 0, setting);
            var name = setting[..separator];
            var value = setting[(separator + 1)..];
            Assert.Matches("^[A-Za-z0-9_.]+$", name);
            Assert.Equal(JsonValueKind.String, JsonDocument.Parse(value).RootElement.ValueKind);
        }
    }

    [Fact]
    public void BackgroundOverridesDisableExactToolNamesWithoutCopyingCredentials()
    {
        var configuration = Json("{\"config\":{\"mcp_servers\":{\"node_repl\":{\"command\":\"node\",\"env\":{\"API_KEY\":\"sk-private\"}},\"server.with.dots\":{\"url\":\"https://private.invalid\",\"enabled\":true},\"quoted\\\"name\":{\"command\":\"private-command\",\"enabled\":false}},\"model_provider\":\"user-provider\"}}");
        var overrides = CliSession.CodexBackgroundOverrides(configuration);
        var expected = Json("{\"mcp_servers\":{\"node_repl\":{\"enabled\":false},\"server.with.dots\":{\"enabled\":false},\"quoted\\\"name\":{\"enabled\":false}}}");
        Assert.True(JsonNode.DeepEquals(expected, overrides), overrides.ToJsonString());
        Assert.True(configuration["config"]!["mcp_servers"]!["server.with.dots"]!["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void BackgroundOverridesDoNotCreateToolEntriesWithoutConfiguredTools()
    {
        foreach (var configuration in new[] { "{}", "{\"config\":{}}", "{\"config\":{\"mcp_servers\":{}}}" })
        {
            Assert.Equal("{}", CliSession.CodexBackgroundOverrides(Json(configuration)).ToJsonString());
        }
    }

    [Fact]
    public void BootstrapToolErrorsAreActionableWithoutExposingConfiguration()
    {
        var diagnostic = SafeCliError.Describe(Json("{\"error\":{\"code\":-32600,\"message\":\"failed to load bootstrap configuration: invalid transport in mcp_servers.\\\"private-name\\\" sk-private\"}}"));
        Assert.True(diagnostic.Contains("后台工具连接配置无效"), diagnostic);
        Assert.DoesNotContain("private", diagnostic);
        diagnostic = SafeCliError.Describe(Json("{\"error\":{\"code\":-32600,\"message\":\"failed to load bootstrap configuration at C:/private/config.toml\"}}"));
        Assert.True(diagnostic.Contains("配置加载或覆盖失败"), diagnostic);
        Assert.DoesNotContain("private", diagnostic);
    }

    [Fact]
    public void ProtocolErrorsKeepCodesWithoutExposingMessagesOrCredentials()
    {
        var error = SafeCliError.Describe(Json("{\"id\":1,\"error\":{\"code\":-32602,\"message\":\"invalid params sk-test-secret Authorization: Bearer hidden\"}}"));
        Assert.Contains("参数不兼容", error);
        Assert.Contains("-32602", error);
        Assert.DoesNotContain("secret", error);
        Assert.DoesNotContain("Bearer", error);
        var unknown = SafeCliError.Describe(Json("{\"id\":401,\"error\":{\"code\":-32000,\"message\":\"fixture failure\"}}"));
        Assert.Contains("-32000", unknown);
        Assert.DoesNotContain("认证失败", unknown);
    }

    [Fact]
    public void ProtocolDiagnosticsKeepOnlyKnownFieldsAndStableIdentifiers()
    {
        var @event = Json("{\"id\":401,\"error\":{\"code\":-32600,\"message\":\"missing field `experimentalRawEvents`; Authorization: Bearer hidden sk-test-secret\"}}");
        var diagnostic = SafeCliError.Describe(@event);
        Assert.True(diagnostic.Contains("缺少字段：experimentalRawEvents"), diagnostic);
        Assert.Contains("-32600", diagnostic);
        Assert.Equal(diagnostic, SafeCliError.Describe(@event));
        Assert.Equal(16, diagnostic.Split("诊断编号 ")[1].Length);
        foreach (var secret in new[] { "Authorization", "Bearer", "hidden", "secret" })
        {
            Assert.DoesNotContain(secret, diagnostic);
        }

        var unknown = SafeCliError.Describe(Json("{\"error\":{\"code\":-32602,\"message\":\"unknown field `sk-test-secret`\"}}"));
        Assert.Contains("字段名未列入安全白名单", unknown);
        Assert.DoesNotContain("secret", unknown);
        var unrelated = SafeCliError.Describe(Json("{\"error\":{\"code\":-32600,\"message\":\"missing field `sk-test-secret`; unrelated `sandbox` configuration\"}}"));
        Assert.Contains("字段名未列入安全白名单", unrelated);
        Assert.DoesNotContain("缺少字段：sandbox", unrelated);
    }

    [Fact]
    public void NestedProtocolErrorsExplainConfigFailureWithoutRawPaths()
    {
        var diagnostic = SafeCliError.Describe(Json("{\"params\":{\"turn\":{\"error\":{\"code\":-32600,\"message\":\"Failed to load config from C:/private/config.toml token=sk-secret\"}}}}"));
        Assert.Contains("-32600", diagnostic);
        Assert.Contains("配置加载或覆盖失败", diagnostic);
        Assert.DoesNotContain("private", diagnostic);
        Assert.DoesNotContain("sk-secret", diagnostic);
    }

    [Fact]
    public void ProtocolDiagnosticsAcceptQuotedFieldsWithoutCopyingTheirValues()
    {
        foreach (var message in new[] { "unknown field \\\"sandboxPolicy\\\", value=sk-private", "unknown field 'sandboxPolicy', value=sk-private" })
        {
            var diagnostic = SafeCliError.Describe(Json($"{{\"error\":{{\"code\":-32602,\"message\":\"{message}\"}}}}"));
            Assert.True(diagnostic.Contains("不支持字段：sandboxPolicy"), diagnostic);
            Assert.DoesNotContain("private", diagnostic);
        }
    }

    [Fact]
    public void CliErrorStringsAndArraysKeepSafeClassification()
    {
        foreach (var @event in new[]
                 {
                     "{\"error\":\"HTTP 401 Authorization: Bearer hidden\"}",
                     "{\"error\":[\"HTTP 401\", \"sk-test-secret\"]}",
                     "{\"params\":{\"error\":\"authentication_error sk-test-secret\"}}",
                     "{\"params\":{\"turn\":{\"error\":[\"HTTP 401\", \"sk-test-secret\"]}}}",
                     "\"HTTP 401 Authorization: Bearer hidden\"",
                     "[\"HTTP 401\", \"sk-test-secret\"]",
                 })
        {
            var error = SafeCliError.Describe(Json(@event));
            Assert.Contains("认证失败", error);
            Assert.DoesNotContain("secret", error);
            Assert.DoesNotContain("hidden", error);
            Assert.DoesNotContain("Bearer", error);
        }
    }

    [Fact]
    public void CodexFailuresRetainTheSafeErrorNotification()
    {
        var turn = new CodexTurn();
        Assert.False(turn.Observe(Json("{\"method\":\"error\",\"params\":{\"error\":{\"message\":\"HTTP 429 Authorization: Bearer hidden\"},\"willRetry\":false}}"), 0.1));
        var error = Assert.Throws<CliException>(() => turn.Observe(Json("{\"method\":\"turn/completed\",\"params\":{\"turn\":{\"status\":\"failed\",\"error\":null,\"items\":[]}}}"), 0.2));
        Assert.Contains("turn/completed", error.Message);
        Assert.Contains("限流", error.Message);
        Assert.DoesNotContain("hidden", error.Message);
    }

    [Fact]
    public void CodexRetriedErrorsDoNotDiscardACompleteAnswerOrUsage()
    {
        var turn = new CodexTurn();
        turn.Observe(Json("{\"method\":\"error\",\"params\":{\"error\":{\"message\":\"connection retry\"},\"willRetry\":true}}"), 0.1);
        turn.Observe(Json("{\"method\":\"item/completed\",\"params\":{\"item\":{\"type\":\"agentMessage\",\"text\":\"完整答案\",\"phase\":\"final_answer\"}}}"), 0.2);
        turn.Observe(Json("{\"method\":\"thread/tokenUsage/updated\",\"params\":{\"tokenUsage\":{\"last\":{\"inputTokens\":40,\"outputTokens\":12,\"cachedInputTokens\":0,\"reasoningOutputTokens\":0}}}}"), 0.3);
        Assert.True(turn.Observe(Json("{\"method\":\"turn/completed\",\"params\":{\"turn\":{\"status\":\"completed\",\"items\":[]}}}"), 0.4));
        var reply = CliReply.Create(KeepAliveFlavor.Codex, turn.Answer, null, turn.Usage, turn.FirstContent);
        Assert.Equal("完整答案", reply.Stats.Answer());
        Assert.Equal(52UL, reply.Stats.ContextTokens(false));
    }

    [Fact]
    public void ClaudeTurnMergesUsageAndRejectsIncompleteStops()
    {
        var turn = new ClaudeTurn();
        Assert.False(turn.Observe(Json("{\"type\":\"system\",\"model\":\"claude-cli\"}"), 0.0));
        Assert.False(turn.Observe(Json("{\"type\":\"stream_event\",\"event\":{\"type\":\"message_start\",\"message\":{\"model\":\"claude-test\",\"usage\":{\"input_tokens\":30,\"output_tokens\":0,\"cache_read_input_tokens\":5}}}}"), 0.1));
        Assert.False(turn.Observe(Json("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"答\"}}}"), 0.2));
        Assert.Equal(0.2, turn.FirstContent);
        Assert.False(turn.Observe(Json("{\"type\":\"stream_event\",\"event\":{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"input_tokens\":0,\"output_tokens\":7}}}"), 0.3));
        Assert.False(turn.Observe(Json("{\"type\":\"assistant\",\"message\":{\"model\":\"claude-test\",\"content\":[{\"type\":\"text\",\"text\":\"完整答案\"}]}}"), 0.4));
        Assert.True(turn.Observe(Json("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"完整答案\"}"), 0.5));
        Assert.Equal("完整答案", turn.Answer);
        Assert.Equal("claude-test", turn.Model);
        Assert.Equal(30, turn.Usage!["input_tokens"]!.GetValue<int>());
        Assert.Equal(7, turn.Usage!["output_tokens"]!.GetValue<int>());
        var reply = CliReply.Create(KeepAliveFlavor.Claude, turn.Answer, turn.Model, turn.Usage, turn.FirstContent);
        Assert.Equal(42UL, reply.Stats.ContextTokens(true));

        var truncated = new ClaudeTurn();
        truncated.Observe(Json("{\"type\":\"stream_event\",\"event\":{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"max_tokens\"},\"usage\":{\"output_tokens\":1}}}"), 0.1);
        var error = Assert.Throws<CliException>(() => truncated.Observe(Json("{\"type\":\"result\",\"subtype\":\"success\",\"result\":\"半截\"}"), 0.2));
        Assert.Equal("Claude 未完整结束本轮文本回复", error.Message);
        var failed = Assert.Throws<CliException>(() => new ClaudeTurn().Observe(Json("{\"type\":\"result\",\"subtype\":\"error_during_execution\",\"is_error\":true,\"result\":\"authentication_error sk-secret\"}"), 0.2));
        Assert.Contains("认证失败", failed.Message);
        Assert.DoesNotContain("sk-secret", failed.Message);
    }

    [Fact]
    public void CommandDiscoveryWrapsPowerShellScriptsAndRejectsRelativeOverrides()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "retry-proxy-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            var script = System.IO.Path.Combine(directory, "codex.ps1");
            System.IO.File.WriteAllText(script, "exit 0");
            Environment.SetEnvironmentVariable("RETRY_PROXY_CODEX_CLI", script);
            var command = CliCommand.Discover(KeepAliveFlavor.Codex);
            Assert.Equal("powershell.exe", command.Program);
            Assert.Equal(new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script }, command.Arguments.ToArray());
            Environment.SetEnvironmentVariable("RETRY_PROXY_CODEX_CLI", "codex.ps1");
            var error = Assert.Throws<CliException>(() => CliCommand.Discover(KeepAliveFlavor.Codex));
            Assert.Equal("RETRY_PROXY_CODEX_CLI 必须指向已安装 CLI 的绝对路径", error.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RETRY_PROXY_CODEX_CLI", null);
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }
}
