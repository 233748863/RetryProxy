using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RetryProxy.Core.Cli;
using RetryProxy.Core.Config;
using RetryProxy.Core.KeepAlive;
using Xunit;

namespace RetryProxy.Tests;

[Collection("cli-environment")]
public sealed class CliReasoningEffortTests
{
    [Fact]
    public async Task TemporaryClaudeAdvertisesBuiltInToolsWithoutEnablingUserTools()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("retry-proxy-tools-");
        try
        {
            var script = Path.Combine(directory.FullName, "capture.ps1");
            var capturePath = Path.Combine(directory.FullName, "capture.json");
            File.WriteAllText(script, CaptureCli, new UTF8Encoding(true));
            var command = new CliCommand("powershell.exe");
            command.Arguments.AddRange(new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script });
            command.Environment.Add(new("RETRY_PROXY_TEST_EFFORT_CAPTURE", capturePath));
            var credential = CliCredential.Create("sk-local", "http://127.0.0.1:18081", "claude-opus-5-5[1m]");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var session = await CliSession.StartAsync(KeepAliveFlavor.Claude, "tool-test", command, credential,
                ReasoningEffort.Default, cancellation.Token);
            while (!File.Exists(capturePath))
            {
                await Task.Delay(20, cancellation.Token);
            }

            var capture = JsonNode.Parse(await File.ReadAllTextAsync(capturePath, cancellation.Token))!;
            var arguments = capture["arguments"]!.AsArray().Select(value => value!.GetValue<string>()).ToArray();
            Assert.DoesNotContain("--tools", arguments);
            Assert.Contains("--strict-mcp-config", arguments);
            Assert.Contains("claude-opus-5-5[1m]", arguments);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    // 捕获真正到达子进程的参数和环境；Codex 完成握手，Claude 等待输入，不调用真实供应商。
    private const string CaptureCli = """
        $utf8 = [Text.UTF8Encoding]::new($false)
        [Console]::InputEncoding = $utf8
        [Console]::OutputEncoding = $utf8
        $ErrorActionPreference = 'Stop'
        $capture = @{ arguments = @($args); effortEnvironment = $env:CLAUDE_CODE_EFFORT_LEVEL } | ConvertTo-Json -Depth 10 -Compress
        $capturePath = $env:RETRY_PROXY_TEST_EFFORT_CAPTURE
        [IO.File]::WriteAllText($capturePath + '.tmp', $capture, $utf8)
        Move-Item -LiteralPath ($capturePath + '.tmp') -Destination $capturePath
        while ($null -ne ($line = [Console]::In.ReadLine())) {
            $message = $line | ConvertFrom-Json
            $result = switch ($message.method) {
                'initialize' { @{} }
                'config/read' { @{ config = @{} } }
                'thread/start' { @{ thread = @{ id = 'effort-test-thread' }; model = 'effort-test-model' } }
            }
            if ($null -ne $message.id) {
                [Console]::WriteLine((@{ id = $message.id; result = $result } | ConvertTo-Json -Depth 10 -Compress))
                [Console]::Out.Flush()
            }
        }
        """;

    [Theory]
    [InlineData(KeepAliveFlavor.Codex, ReasoningEffort.Default, null)]
    [InlineData(KeepAliveFlavor.Codex, ReasoningEffort.Low, "low")]
    [InlineData(KeepAliveFlavor.Codex, ReasoningEffort.Ultra, "ultra")]
    [InlineData(KeepAliveFlavor.Claude, ReasoningEffort.Default, null)]
    [InlineData(KeepAliveFlavor.Claude, ReasoningEffort.Low, "low")]
    [InlineData(KeepAliveFlavor.Claude, ReasoningEffort.Max, "max")]
    public async Task SelectedEffortReachesTheClientAndDefaultPreservesInheritedSettings(
        KeepAliveFlavor flavor, ReasoningEffort effort, string? expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var directory = Directory.CreateTempSubdirectory("retry-proxy-effort-");
        try
        {
            var script = Path.Combine(directory.FullName, "capture.ps1");
            var capturePath = Path.Combine(directory.FullName, "capture.json");
            File.WriteAllText(script, CaptureCli, new UTF8Encoding(true));
            var command = new CliCommand("powershell.exe");
            command.Arguments.AddRange(new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script });
            command.Environment.Add(new("RETRY_PROXY_TEST_EFFORT_CAPTURE", capturePath));
            command.Environment.Add(new("CLAUDE_CODE_EFFORT_LEVEL", "high"));

            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var session = await CliSession.StartAsync(flavor, "effort-test", command, null, effort, cancellation.Token);
            while (!File.Exists(capturePath))
            {
                await Task.Delay(20, cancellation.Token);
            }
            var capture = JsonNode.Parse(await File.ReadAllTextAsync(capturePath, cancellation.Token))!;
            var arguments = capture["arguments"]!.AsArray().Select(value => value!.GetValue<string>()).ToArray();
            if (flavor == KeepAliveFlavor.Codex)
            {
                if (expected is null)
                {
                    Assert.DoesNotContain(arguments, argument => argument.StartsWith("model_reasoning_effort=", StringComparison.Ordinal));
                }
                else
                {
                    Assert.Contains($"model_reasoning_effort=\"{expected}\"", arguments);
                }
            }
            else
            {
                Assert.Contains("--tools", arguments);
                var settings = JsonNode.Parse(arguments[Array.IndexOf(arguments, "--settings") + 1])!;
                if (expected is null)
                {
                    Assert.DoesNotContain("--effort", arguments);
                    Assert.Null(settings["env"]?["CLAUDE_CODE_EFFORT_LEVEL"]);
                    Assert.Equal("high", capture["effortEnvironment"]!.GetValue<string>());
                }
                else
                {
                    Assert.Equal(expected, arguments[Array.IndexOf(arguments, "--effort") + 1]);
                    Assert.Equal(expected, settings["env"]!["CLAUDE_CODE_EFFORT_LEVEL"]!.GetValue<string>());
                    Assert.Equal(expected, capture["effortEnvironment"]!.GetValue<string>());
                }
            }
            Assert.Equal("high", command.Environment.Single(pair => pair.Key == "CLAUDE_CODE_EFFORT_LEVEL").Value);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
