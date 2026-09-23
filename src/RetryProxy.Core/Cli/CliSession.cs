using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RetryProxy.Core.Internal;
using RetryProxy.Core.KeepAlive;
using RetryProxy.Core.Stats;

namespace RetryProxy.Core.Cli;

/// <summary>CLI 一轮问答的结果：经 ResponseStats 归一化后的用量与答案。</summary>
internal sealed class CliReply
{
    private CliReply(ResponseStats stats, string? model, double? firstContentSeconds)
    {
        Stats = stats;
        Model = model;
        FirstContentSeconds = firstContentSeconds;
    }

    public ResponseStats Stats { get; }

    public string? Model { get; }

    public double? FirstContentSeconds { get; set; }

    public static CliReply Create(KeepAliveFlavor flavor, string answer, string? model, JsonNode? usage, double? firstContentSeconds)
    {
        if (answer.Trim().Length == 0)
        {
            throw new CliException("CLI 未返回完整的文本答案");
        }

        var headers = new HeaderList();
        headers.Set("content-type", "application/json");
        string path;
        JsonObject body;
        if (flavor == KeepAliveFlavor.Claude)
        {
            path = "/v1/messages";
            body = new JsonObject
            {
                ["type"] = "message",
                ["model"] = model,
                ["stop_reason"] = "end_turn",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = answer }),
                ["usage"] = usage?.DeepClone(),
            };
        }
        else
        {
            path = "/v1/responses";
            body = new JsonObject
            {
                ["model"] = model,
                ["status"] = "completed",
                ["output"] = new JsonArray(new JsonObject
                {
                    ["type"] = "message",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = answer }),
                }),
                ["usage"] = usage?.DeepClone(),
            };
        }

        var stats = new ResponseStats(headers, path, model).WithAnswerCapture();
        var elapsed = firstContentSeconds ?? 0.0;
        stats.Observe(Encoding.UTF8.GetBytes(JsonText.Serialize(body)), elapsed);
        stats.Finish(elapsed);
        if (stats.Answer() is null)
        {
            throw new CliException("CLI 答案为空或超过 2 MiB 保护值");
        }

        return new CliReply(stats, model, firstContentSeconds);
    }
}

/// <summary>
/// 一个后台 CLI 子进程（对应 keepalive_cli.rs 的 CliSession）：临时目录、三路管道、Job Object 保护；
/// Codex 走 app-server JSON-RPC，Claude Code 走 stream-json。
/// </summary>
internal sealed class CliSession : IDisposable
{
    private const int MaxEventBytes = 2 * 1024 * 1024;
    internal const string InterviewInstructions = "请用中文回答当前这道初级 Java 面试题，解释准确，控制在二到五句话，不要反问，不调用工具，不读取或修改任何文件。";
    internal const string CodexCredentialEnv = "RETRY_PROXY_PREPARE_KEY";
    internal const string CodexCredentialProvider = "retry_proxy_prepare";

    private static readonly string[] CodexSettings =
    {
        "features.shell_tool=false",
        "features.unified_exec=false",
        "features.hooks=false",
        "features.plugins=false",
        "features.apps=false",
        "features.multi_agent=false",
        "features.view_image=false",
        "web_search=\"disabled\"",
        "project_doc_max_bytes=0",
    };

    private readonly KeepAliveFlavor _flavor;
    private readonly Process _child;
    private readonly Stream _stdin;
    private readonly Stream _stdout;
    private readonly byte[] _readBuffer = new byte[64 * 1024];
    private int _readStart;
    private int _readEnd;
    private readonly ProcessJob? _job;
    private readonly string _directory;
    private string _threadId = string.Empty;
    private string? _model;
    private long _nextId;
    private bool _disposed;

    private CliSession(KeepAliveFlavor flavor, Process child, ProcessJob? job, string directory)
    {
        _flavor = flavor;
        _child = child;
        _job = job;
        _directory = directory;
        _stdin = child.StandardInput.BaseStream;
        _stdout = child.StandardOutput.BaseStream;
    }

    /// <summary>Claude Code 的 <c>--settings</c> 覆盖用户层 env：只走 Bearer，清空 x-api-key。</summary>
    internal static string ClaudeCredentialSettings(CliCredential credential)
    {
        return JsonText.Serialize(new JsonObject
        {
            ["disableAllHooks"] = true,
            ["env"] = new JsonObject
            {
                ["ANTHROPIC_AUTH_TOKEN"] = credential.ApiKey,
                ["ANTHROPIC_API_KEY"] = string.Empty,
                ["ANTHROPIC_BASE_URL"] = credential.BaseUrl,
            },
        });
    }

    /// <summary>Codex 通过临时 provider 的 env_key 读取密钥，密钥不出现在命令行。</summary>
    internal static List<string> CodexCredentialOverrides(CliCredential credential)
    {
        var provider = $"model_providers.{CodexCredentialProvider}";
        var overrides = new List<string>
        {
            $"model_provider=\"{CodexCredentialProvider}\"",
            $"{provider}.name=\"Retry Proxy 指定 Key 准备\"",
            $"{provider}.base_url={JsonText.Serialize(JsonValue.Create($"{credential.BaseUrl}/v1"))}",
            $"{provider}.wire_api=\"responses\"",
            $"{provider}.env_key=\"{CodexCredentialEnv}\"",
        };
        if (credential.Model is { } model)
        {
            overrides.Add($"model={JsonText.Serialize(JsonValue.Create(model))}");
        }

        return overrides;
    }

    /// <summary>把用户配置里的每个 MCP 服务按原名禁用，不复制其余字段。</summary>
    internal static JsonObject CodexBackgroundOverrides(JsonNode? configuration)
    {
        var servers = new JsonObject();
        if (configuration is JsonObject root
            && root.TryGetPropertyValue("config", out var config) && config is JsonObject configObject
            && configObject.TryGetPropertyValue("mcp_servers", out var list) && list is JsonObject listObject)
        {
            foreach (var pair in listObject)
            {
                servers[pair.Key] = new JsonObject { ["enabled"] = false };
            }
        }

        return servers.Count == 0 ? new JsonObject() : new JsonObject { ["mcp_servers"] = servers };
    }

    public static async Task<CliSession> StartAsync(KeepAliveFlavor flavor, string marker, CliCommand? commandOverride, CliCredential? credential, CancellationToken cancellationToken)
    {
        var configured = commandOverride?.Clone() ?? CliCommand.Discover(flavor);
        string directory;
        try
        {
            directory = Path.Combine(Path.GetTempPath(), $"retry-proxy-keepalive-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new CliException("无法创建后台会话工作目录");
        }

        var start = new ProcessStartInfo(configured.Program)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in configured.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in configured.Environment)
        {
            start.Environment[name] = value;
        }

        if (flavor == KeepAliveFlavor.Claude)
        {
            var settings = credential is null ? "{\"disableAllHooks\":true}" : ClaudeCredentialSettings(credential);
            foreach (var argument in new[]
                     {
                         "--print", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
                         "--include-partial-messages", "--no-session-persistence", "--tools", string.Empty,
                         "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}", "--disable-slash-commands",
                         "--settings", settings, "--append-system-prompt", InterviewInstructions,
                     })
            {
                start.ArgumentList.Add(argument);
            }

            if (credential is not null)
            {
                if (credential.Model is { } model)
                {
                    start.ArgumentList.Add("--model");
                    start.ArgumentList.Add(model);
                }

                // 进程环境与 --settings 双重覆盖：无论 CLI 以哪一层为准，都只会用本次输入的 Key 与本通道地址。
                start.Environment["ANTHROPIC_AUTH_TOKEN"] = credential.ApiKey;
                start.Environment.Remove("ANTHROPIC_API_KEY");
                start.Environment["ANTHROPIC_BASE_URL"] = credential.BaseUrl;
            }

            var existing = configured.Environment.FirstOrDefault(pair => pair.Key == "ANTHROPIC_CUSTOM_HEADERS").Value
                ?? Environment.GetEnvironmentVariable("ANTHROPIC_CUSTOM_HEADERS")
                ?? string.Empty;
            var kept = existing.Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .Where(line => !string.Equals(line.Split(':')[0].Trim(), "x-retry-keepalive", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var headers = string.Join("\n", kept);
            if (headers.Length > 0)
            {
                headers += "\n";
            }

            headers += $"x-retry-keepalive: {marker}";
            start.Environment["ANTHROPIC_CUSTOM_HEADERS"] = headers;
            start.Environment.Remove("CLAUDECODE");
        }
        else
        {
            start.ArgumentList.Add("app-server");
            start.ArgumentList.Add("--listen");
            start.ArgumentList.Add("stdio://");
            foreach (var setting in CodexSettings)
            {
                start.ArgumentList.Add("-c");
                start.ArgumentList.Add(setting);
            }

            if (credential is not null)
            {
                foreach (var setting in CodexCredentialOverrides(credential))
                {
                    start.ArgumentList.Add("-c");
                    start.ArgumentList.Add(setting);
                }

                start.Environment[CodexCredentialEnv] = credential.ApiKey;
            }
        }

        Process child;
        try
        {
            child = Process.Start(start) ?? throw new CliException($"无法启动 {flavor.Label()} CLI：Other");
        }
        catch (Win32Exception error)
        {
            TryDeleteDirectory(directory);
            throw new CliException($"无法启动 {flavor.Label()} CLI：{IoKind(error)}");
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            TryDeleteDirectory(directory);
            throw new CliException($"无法启动 {flavor.Label()} CLI：Other");
        }

        ProcessJob? job = null;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                job = ProcessJob.Attach(child);
            }
            catch (CliException)
            {
                TryKill(child);
                child.Dispose();
                TryDeleteDirectory(directory);
                throw;
            }
        }

        var stderr = child.StandardError.BaseStream;
        _ = Task.Run(async () =>
        {
            try
            {
                await stderr.CopyToAsync(Stream.Null).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        });

        var session = new CliSession(flavor, child, job, directory);
        if (flavor != KeepAliveFlavor.Claude)
        {
            try
            {
                await session.InitializeCodexAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        return session;
    }

    private static string IoKind(Win32Exception error) => error.NativeErrorCode switch
    {
        2 or 3 => "NotFound",
        5 => "PermissionDenied",
        _ => "Other",
    };

    private async Task WriteAsync(JsonNode value, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonText.Serialize(value) + "\n");
        try
        {
            await _stdin.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new CliException("CLI 已退出，无法发送消息");
        }

        try
        {
            await _stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new CliException("CLI 输入管道已关闭");
        }
    }

    /// <summary>管道读取不支持真正的取消：取消时放弃等待，读任务随进程结束而结束。</summary>
    private async Task<int> FillAsync(CancellationToken cancellationToken)
    {
        if (_readStart == _readEnd)
        {
            _readStart = 0;
            _readEnd = 0;
        }

        var read = _stdout.ReadAsync(_readBuffer.AsMemory(_readEnd), CancellationToken.None).AsTask();
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => cancelled.TrySetResult(true));
        var completed = await Task.WhenAny(read, cancelled.Task).ConfigureAwait(false);
        if (completed != read)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        var count = await read.ConfigureAwait(false);
        _readEnd += count;
        return count;
    }

    /// <summary>把缓冲区里的字节接到当前行上；遇到换行返回 true。</summary>
    private bool TakeLineFragment(MemoryStream line)
    {
        var available = new ReadOnlySpan<byte>(_readBuffer, _readStart, _readEnd - _readStart);
        var newline = available.IndexOf((byte)'\n');
        var take = newline < 0 ? available.Length : newline + 1;
        if (line.Length + take > MaxEventBytes)
        {
            throw new CliException("CLI 单条输出超过 2 MiB 保护值");
        }

        line.Write(available[..take]);
        _readStart += take;
        return newline >= 0;
    }

    private async Task<JsonNode> ReadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = new MemoryStream();
            while (true)
            {
                if (_readStart == _readEnd)
                {
                    int count;
                    try
                    {
                        count = await FillAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        throw new CliException("CLI 输出读取失败");
                    }

                    if (count == 0)
                    {
                        throw new CliException("CLI 在完整回复前退出");
                    }
                }

                if (TakeLineFragment(line))
                {
                    break;
                }
            }

            JsonNode? value;
            try
            {
                value = JsonNode.Parse(line.GetBuffer().AsSpan(0, (int)line.Length));
            }
            catch (JsonException)
            {
                continue;
            }

            if (value is JsonObject)
            {
                return value;
            }
        }
    }

    private static bool HasError(JsonObject node) => node.TryGetPropertyValue("error", out var error) && error is not null && error.GetValueKind() != JsonValueKind.Null;

    private static long? IdOf(JsonObject node) => node.TryGetPropertyValue("id", out var id) && id is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

    private async Task<JsonNode?> RpcAsync(string method, JsonNode parameters, CancellationToken cancellationToken)
    {
        var requestId = ++_nextId;
        try
        {
            await WriteAsync(new JsonObject { ["id"] = requestId, ["method"] = method, ["params"] = parameters }, cancellationToken).ConfigureAwait(false);
        }
        catch (CliException error)
        {
            throw new CliException($"客户端调用 {method} 失败：{error.Message}");
        }

        while (true)
        {
            JsonObject @event;
            try
            {
                @event = (JsonObject)await ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (CliException error)
            {
                throw new CliException($"客户端调用 {method} 失败：{error.Message}");
            }

            if (IdOf(@event) == requestId && !@event.ContainsKey("method"))
            {
                if (HasError(@event))
                {
                    throw new CliException($"客户端调用 {method} 失败：{SafeCliError.Describe(@event)}");
                }

                return @event.TryGetPropertyValue("result", out var result) ? result?.DeepClone() : null;
            }

            await RejectToolRequestAsync(@event, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RejectToolRequestAsync(JsonObject @event, CancellationToken cancellationToken)
    {
        if (@event.TryGetPropertyValue("id", out var id) && id is not null && @event.ContainsKey("method"))
        {
            await WriteAsync(new JsonObject
            {
                ["id"] = id.DeepClone(),
                ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Background keepalive does not execute tools" },
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task InitializeCodexAsync(CancellationToken cancellationToken)
    {
        await RpcAsync("initialize", new JsonObject
        {
            ["clientInfo"] = new JsonObject { ["name"] = "codex_cli_rs", ["version"] = "2.0.0" },
            ["capabilities"] = new JsonObject { ["experimentalApi"] = true },
        }, cancellationToken).ConfigureAwait(false);
        await WriteAsync(new JsonObject { ["method"] = "initialized" }, cancellationToken).ConfigureAwait(false);
        var configuration = await RpcAsync("config/read", new JsonObject { ["includeLayers"] = false }, cancellationToken).ConfigureAwait(false);
        var overrides = CodexBackgroundOverrides(configuration);
        var reply = await RpcAsync("thread/start", new JsonObject
        {
            ["cwd"] = _directory,
            ["ephemeral"] = true,
            ["approvalPolicy"] = "never",
            ["sandbox"] = "read-only",
            ["developerInstructions"] = InterviewInstructions,
            ["config"] = overrides,
        }, cancellationToken).ConfigureAwait(false);
        var threadId = reply is JsonObject replyObject
            && replyObject.TryGetPropertyValue("thread", out var thread) && thread is JsonObject threadObject
            && threadObject.TryGetPropertyValue("id", out var idNode) && idNode is JsonValue idValue && idValue.TryGetValue<string>(out var text)
            && text.Length > 0
            ? text
            : throw new CliException("Codex 未返回后台会话 ID");
        _threadId = threadId;
        _model = reply is JsonObject modelSource && modelSource.TryGetPropertyValue("model", out var model) && model is JsonValue modelValue && modelValue.TryGetValue<string>(out var modelText)
            ? modelText
            : null;
    }

    public Task<CliReply> AskAsync(string question, string marker, CancellationToken cancellationToken)
    {
        return _flavor == KeepAliveFlavor.Claude ? AskClaudeAsync(question, cancellationToken) : AskCodexAsync(question, marker, cancellationToken);
    }

    private async Task<CliReply> AskCodexAsync(string question, string marker, CancellationToken cancellationToken)
    {
        var requestId = ++_nextId;
        var started = Stopwatch.StartNew();
        await WriteAsync(new JsonObject
        {
            ["id"] = requestId,
            ["method"] = "turn/start",
            ["params"] = new JsonObject
            {
                ["threadId"] = _threadId,
                ["input"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = question }),
                ["responsesapiClientMetadata"] = new JsonObject { ["retry_proxy_keepalive"] = marker },
            },
        }, cancellationToken).ConfigureAwait(false);
        var turn = new CodexTurn();
        while (true)
        {
            var @event = (JsonObject)await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (IdOf(@event) == requestId && HasError(@event))
            {
                throw new CliException($"客户端调用 turn/start 失败：{SafeCliError.Describe(@event)}");
            }

            await RejectToolRequestAsync(@event, cancellationToken).ConfigureAwait(false);
            if (@event.TryGetPropertyValue("params", out var parameters) && parameters is JsonObject parameterObject
                && parameterObject.TryGetPropertyValue("threadId", out var thread) && thread is JsonValue threadValue
                && threadValue.TryGetValue<string>(out var threadText) && threadText != _threadId)
            {
                continue;
            }

            if (turn.Observe(@event, started.Elapsed.TotalSeconds))
            {
                return CliReply.Create(_flavor, turn.Answer, _model, turn.Usage, turn.FirstContent);
            }
        }
    }

    private async Task<CliReply> AskClaudeAsync(string question, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        await WriteAsync(new JsonObject
        {
            ["type"] = "user",
            ["session_id"] = _threadId,
            ["parent_tool_use_id"] = null,
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = question },
        }, cancellationToken).ConfigureAwait(false);
        var turn = new ClaudeTurn();
        while (true)
        {
            var @event = (JsonObject)await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (@event.TryGetPropertyValue("session_id", out var session) && session is JsonValue sessionValue && sessionValue.TryGetValue<string>(out var sessionId))
            {
                _threadId = sessionId;
            }

            if (StringOf(@event, "type") == "control_request")
            {
                await WriteAsync(new JsonObject
                {
                    ["type"] = "control_response",
                    ["response"] = new JsonObject
                    {
                        ["subtype"] = "error",
                        ["request_id"] = @event["request_id"]?.DeepClone(),
                        ["error"] = "Background keepalive does not execute tools",
                    },
                }, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (turn.Observe(@event, started.Elapsed.TotalSeconds))
            {
                _model = turn.Model ?? _model;
                return CliReply.Create(_flavor, turn.Answer, _model, turn.Usage, turn.FirstContent);
            }
        }
    }

    internal static string? StringOf(JsonNode? node, string name)
    {
        return node is JsonObject obj && obj.TryGetPropertyValue(name, out var value) && value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text) ? text : null;
    }

    private static void TryKill(Process child)
    {
        try
        {
            child.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _job?.Terminate(_child);
        TryKill(_child);
        _job?.Dispose();
        try
        {
            _stdin.Dispose();
        }
        catch (Exception)
        {
        }

        _child.Dispose();
        TryDeleteDirectory(_directory);
    }
}

/// <summary>Codex app-server 一轮里的事件累积（对应 CodexTurn）。</summary>
internal sealed class CodexTurn
{
    public string Answer { get; private set; } = string.Empty;

    public JsonNode? Usage { get; private set; }

    public double? FirstContent { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>返回 true 表示本轮结束；失败抛 <see cref="CliException"/>。</summary>
    public bool Observe(JsonNode @event, double elapsed)
    {
        var parameters = @event["params"];
        switch (CliSession.StringOf(@event, "method"))
        {
            case "item/agentMessage/delta":
                if (CliSession.StringOf(parameters, "delta") is { Length: > 0 })
                {
                    FirstContent ??= elapsed;
                }

                break;
            case "item/completed":
                ObserveItem(parameters?["item"], elapsed);
                break;
            case "thread/tokenUsage/updated":
                var usage = parameters?["tokenUsage"]?["last"];
                Usage = new JsonObject
                {
                    ["input_tokens"] = usage?["inputTokens"]?.DeepClone(),
                    ["output_tokens"] = usage?["outputTokens"]?.DeepClone(),
                    ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = usage?["cachedInputTokens"]?.DeepClone() },
                    ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = usage?["reasoningOutputTokens"]?.DeepClone() },
                };
                break;
            case "error":
                LastError = SafeCliError.Describe(@event);
                break;
            case "turn/completed":
                var turn = parameters?["turn"];
                if (CliSession.StringOf(turn, "status") != "completed")
                {
                    var turnError = turn?["error"];
                    var reason = turnError is not null && turnError.GetValueKind() != JsonValueKind.Null
                        ? SafeCliError.Describe(@event)
                        : LastError ?? SafeCliError.Describe(@event);
                    throw new CliException($"接收回答 turn/completed 失败：{reason}");
                }

                if (turn?["items"] is JsonArray items)
                {
                    foreach (var item in items)
                    {
                        ObserveItem(item, elapsed);
                    }
                }

                return true;
        }

        return false;
    }

    private void ObserveItem(JsonNode? item, double elapsed)
    {
        if (CliSession.StringOf(item, "type") == "agentMessage" && CliSession.StringOf(item, "phase") != "commentary")
        {
            if (CliSession.StringOf(item, "text") is { Length: > 0 } text)
            {
                Answer = text;
                FirstContent ??= elapsed;
            }
        }
    }
}

/// <summary>Claude Code stream-json 一轮里的事件累积（对应 ClaudeTurn）。</summary>
internal sealed class ClaudeTurn
{
    public string Answer { get; private set; } = string.Empty;

    public string? Model { get; private set; }

    public JsonNode? Usage { get; private set; }

    public string? StopReason { get; private set; }

    public double? FirstContent { get; private set; }

    public bool Observe(JsonNode @event, double elapsed)
    {
        switch (CliSession.StringOf(@event, "type"))
        {
            case "system":
                Model = CliSession.StringOf(@event, "model");
                break;
            case "stream_event":
                var stream = @event["event"];
                switch (CliSession.StringOf(stream, "type"))
                {
                    case "message_start":
                        Message(stream?["message"]);
                        break;
                    case "message_delta":
                        StopReason = CliSession.StringOf(stream?["delta"], "stop_reason") ?? StopReason;
                        if (stream?["usage"] is JsonObject usage)
                        {
                            if (Usage is not JsonObject)
                            {
                                Usage = new JsonObject();
                            }

                            var target = (JsonObject)Usage;
                            foreach (var pair in usage)
                            {
                                var isZeroInput = pair.Key == "input_tokens" && pair.Value is JsonValue value && value.TryGetValue<ulong>(out var number) && number == 0;
                                if (!isZeroInput || !target.ContainsKey(pair.Key))
                                {
                                    target[pair.Key] = pair.Value?.DeepClone();
                                }
                            }
                        }

                        break;
                    case "content_block_delta":
                    case "content_block_start":
                        var text = CliSession.StringOf(stream?["delta"], "text") ?? CliSession.StringOf(stream?["content_block"], "text");
                        if (text is { Length: > 0 })
                        {
                            FirstContent ??= elapsed;
                        }

                        break;
                }

                break;
            case "assistant":
                Message(@event["message"]);
                if (Answer.Length > 0)
                {
                    FirstContent ??= elapsed;
                }

                break;
            case "result":
                var isError = @event["is_error"] is JsonValue errorFlag && errorFlag.TryGetValue<bool>(out var flag) && flag;
                if (CliSession.StringOf(@event, "subtype") != "success" || isError)
                {
                    throw new CliException(SafeCliError.Describe(@event));
                }

                if (StopReason is not null && StopReason is not ("end_turn" or "stop_sequence"))
                {
                    throw new CliException("Claude 未完整结束本轮文本回复");
                }

                if (CliSession.StringOf(@event, "result") is { } answer)
                {
                    Answer = answer;
                }

                if (Usage is not JsonObject)
                {
                    Usage = @event["usage"]?.DeepClone();
                }

                if (Answer.Length > 0)
                {
                    FirstContent ??= elapsed;
                }

                return true;
        }

        return false;
    }

    private void Message(JsonNode? message)
    {
        if (CliSession.StringOf(message, "model") is { } model)
        {
            Model = model;
        }

        if (message?["usage"] is JsonObject usage)
        {
            Usage = usage.DeepClone();
        }

        if (CliSession.StringOf(message, "stop_reason") is { } reason)
        {
            StopReason = reason;
        }

        if (message?["content"] is JsonArray content)
        {
            Answer = string.Join("\n", content
                .Where(part => CliSession.StringOf(part, "type") == "text")
                .Select(part => CliSession.StringOf(part, "text"))
                .Where(text => text is not null));
        }
    }
}
