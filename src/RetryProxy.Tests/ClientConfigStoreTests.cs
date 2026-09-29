using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RetryProxy.Core.Cli;
using RetryProxy.Core.Client;
using RetryProxy.Core.Config;
using Xunit;

namespace RetryProxy.Tests;

/// <summary>所有内容均人工构造，只在随机临时目录内测试；编辑器仅模拟合并，不调用真实客户端编解码器。</summary>
public sealed class ClientConfigStoreTests : IDisposable
{
    private const string Original = "{\"unmanaged\":\"redacted-user-setting\",\"managed\":\"old\"}";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RetryProxy.ClientStore.Tests", Guid.NewGuid().ToString("N"));
    private readonly FakeEditor _editor = new();
    private string ConfigPath => Path.Combine(_root, "client", "settings.json");
    private string BackupRoot => Path.Combine(_root, "backup");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void ReadProfile_MissingFilesAreEmptyAndHashIsEmpty()
    {
        _editor.ClientType = ClientType.Codex;
        var store = Store();
        store.ReadProfile();
        Assert.Equal(string.Empty, _editor.ReadText);
        Assert.Null(_editor.AuthText);
        Assert.Equal(string.Empty, store.CurrentHash());
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void ReadProfile_CodexAuthIsOptionalAndNeverWrittenOrBackedUp()
    {
        _editor.ClientType = ClientType.Codex;
        Put(Original);
        var authPath = Path.Combine(Path.GetDirectoryName(ConfigPath)!, "auth.json");
        const string auth = "{\"OPENAI_API_KEY\":\"redacted-api-key\"}";
        File.WriteAllText(authPath, auth);
        var store = Store();
        store.ReadProfile();
        Assert.Equal(Original, _editor.ReadText);
        Assert.Equal(auth, _editor.AuthText);
        var state = store.Apply(Request(ClientType.Codex), new(), true, _ => { });
        Assert.Equal(auth, File.ReadAllText(authPath));
        Assert.Single(Directory.GetFiles(state.BackupPath));
    }

    [Fact]
    public void ReadProfile_ClaudeDoesNotReadSiblingAuth()
    {
        Put(Original);
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(ConfigPath)!, "auth.json"));
        Store().ReadProfile();
        Assert.Null(_editor.AuthText);
    }

    [Fact]
    public void CurrentHash_HashesExactBytesAndDistinguishesEmptyFromMissing()
    {
        Put(string.Empty);
        var store = Store();
        Assert.Equal(Convert.ToHexString(SHA256.HashData([])), store.CurrentHash());
        var bytes = new byte[] { 0xef, 0xbb, 0xbf, 0x7b, 0x7d };
        File.WriteAllBytes(ConfigPath, bytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), store.CurrentHash());
        File.Delete(ConfigPath);
        Assert.Equal(string.Empty, store.CurrentHash());
    }

    [Theory]
    [InlineData(ClientType.Claude, "http://127.0.0.1:18081", true)]
    [InlineData(ClientType.Claude, "http://LOCALHOST:18081/", true)]
    [InlineData(ClientType.Claude, "https://127.0.0.1:18081", false)]
    [InlineData(ClientType.Claude, "http://127.0.0.1:18080", false)]
    [InlineData(ClientType.Claude, "http://127.0.0.1:18081/v1", false)]
    [InlineData(ClientType.Claude, "http://127.0.0.1:18081?token=x", false)]
    [InlineData(ClientType.Claude, "http://user@127.0.0.1:18081", false)]
    [InlineData(ClientType.Claude, "http://127.0.0.1.evil.invalid:18081", false)]
    [InlineData(ClientType.Claude, "http://127.1:18081", false)]
    [InlineData(ClientType.Codex, "http://127.0.0.1:18080/v1", true)]
    [InlineData(ClientType.Codex, "http://localhost:18080/v1/", true)]
    [InlineData(ClientType.Codex, "http://localhost:18080", false)]
    [InlineData(ClientType.Codex, "http://localhost:18080/V1", false)]
    [InlineData(ClientType.Codex, "http://localhost:18080/v1/responses", false)]
    [InlineData(ClientType.Codex, "http://localhost:18080/other/../v1", false)]
    [InlineData(ClientType.Codex, "http://localhost:18080/v1#fragment", false)]
    [InlineData(ClientType.Codex, "http://localhost:18080/v1//", false)]
    public void IsTakenOver_RequiresExactClientEndpoint(ClientType client, string url, bool expected)
    {
        _editor.ClientType = client;
        _editor.Profile = Profile(client, url);
        Assert.Equal(expected, Store().IsTakenOver(Channel(client), Port(client)));
    }

    [Theory]
    [InlineData(ClientType.Claude, "wrong-token", ClaudeAuthMode.Bearer, false, "responses")]
    [InlineData(ClientType.Claude, "local-redacted", ClaudeAuthMode.ApiKey, false, "responses")]
    [InlineData(ClientType.Claude, "local-redacted", ClaudeAuthMode.Bearer, true, "responses")]
    [InlineData(ClientType.Codex, "local-redacted", ClaudeAuthMode.Bearer, false, "chat")]
    [InlineData(ClientType.Codex, "local-redacted", ClaudeAuthMode.Bearer, false, "")]
    [InlineData(ClientType.Codex, "local-redacted", ClaudeAuthMode.ApiKey, false, "responses")]
    public void IsTakenOver_RequiresTokenAuthAndWireApi(ClientType client, string key, ClaudeAuthMode auth, bool helper, string wire)
    {
        _editor.ClientType = client;
        _editor.Profile = new ClientProfile
        {
            ClientType = client, BaseUrl = client == ClientType.Claude ? "http://127.0.0.1:18081" : "http://127.0.0.1:18080/v1",
            ApiKey = key, AuthMode = auth, HasApiKeyHelper = helper, WireApi = wire,
        };
        Assert.False(Store().IsTakenOver(Channel(client), Port(client)));
    }

    [Fact]
    public void IsTakenOver_RejectsEmptyTokenClientMismatchAndInvalidPort()
    {
        _editor.Profile = Profile(ClientType.Claude, "http://127.0.0.1:18081");
        var store = Store();
        Assert.False(store.IsTakenOver(new ChannelSnapshot { ClientType = ClientType.Claude }, 18081));
        Assert.False(store.IsTakenOver(Channel(ClientType.Codex), 18080));
        Assert.False(store.IsTakenOver(Channel(ClientType.Claude), 0));
        Assert.False(store.IsTakenOver(Channel(ClientType.Claude), 65536));
    }

    [Fact]
    public void Apply_BackupPrecedesWriteAndPersistReceivesIndependentState()
    {
        Put(Original);
        var previous = new ClientTakeoverState();
        var originalState = previous.Clone();
        ClientTakeoverState? persisted = null;
        var store = Store(clock: () => new DateTime(2026, 9, 29, 10, 20, 30));
        var result = store.Apply(Request(), previous, true, state =>
        {
            Assert.Equal("new", JsonNode.Parse(File.ReadAllText(ConfigPath))!["managed"]!.GetValue<string>());
            Assert.Equal(Original, File.ReadAllText(Path.Combine(state.BackupPath, "settings.json")));
            Assert.Equal(store.CurrentHash(), state.LastWrittenHash);
            persisted = state;
            state.Enabled = false;
            state.BackupPath = "changed-by-callback";
        });
        Assert.NotSame(previous, result);
        Assert.NotSame(result, persisted);
        Assert.Equal(originalState, previous);
        Assert.True(result.Enabled);
        Assert.Equal(ConfigPath, result.ConfigPath);
        Assert.StartsWith(Path.Combine(BackupRoot, "claude", "20260929-102030-"), result.BackupPath);
        Assert.Equal("redacted-user-setting", JsonNode.Parse(File.ReadAllText(ConfigPath))!["unmanaged"]!.GetValue<string>());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(ConfigPath)!, "*.tmp"));
    }

    [Fact]
    public void Apply_MissingOriginalCreatesMarkerAndNewFile()
    {
        var state = Store().Apply(Request(), new(), true, _ => { });
        Assert.True(File.Exists(ConfigPath));
        Assert.True(File.Exists(Path.Combine(state.BackupPath, "settings.json" + ClientConfigStore.MissingFileMarkerSuffix)));
        Assert.Equal(0, new FileInfo(Path.Combine(state.BackupPath, "settings.json.missing")).Length);
    }

    [Fact]
    public void Apply_OffThenOnKeepsFirstBackup()
    {
        Put(Original);
        var store = Store();
        var first = store.Apply(Request(), new(), true, _ => { });
        var off = store.Apply(new ClientConfigRequest { Channel = Channel(ClientType.Claude), UseProxy = false }, first, false, _ => { });
        Assert.False(off.Enabled);
        var again = store.Apply(Request(), off, true, _ => { });
        Assert.Equal(first.BackupPath, again.BackupPath);
        Assert.Equal(Original, File.ReadAllText(Path.Combine(again.BackupPath, "settings.json")));
        Assert.Single(Directory.GetDirectories(Path.Combine(BackupRoot, "claude")));
    }

    [Fact]
    public void Apply_ConfigPathChangeCreatesNewBackupWithoutOverwritingFirst()
    {
        Put(Original);
        var first = Store().Apply(Request(), new(), true, _ => { });
        var newPath = Path.Combine(_root, "other-client", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
        File.WriteAllText(newPath, "{\"other\":true}");
        var second = new ClientConfigStore(BackupRoot, _editor, newPath).Apply(Request(), first, true, _ => { });
        Assert.NotEqual(first.BackupPath, second.BackupPath);
        Assert.Equal("{\"other\":true}", File.ReadAllText(Path.Combine(second.BackupPath, "settings.json")));
        Assert.Equal(Original, File.ReadAllText(Path.Combine(first.BackupPath, "settings.json")));
    }

    [Fact]
    public void Apply_ExternalEditsAreMergedFromLatestContent()
    {
        Put(Original);
        var store = Store();
        var first = store.Apply(Request(), new(), true, _ => { });
        Put("{\"unmanaged\":\"external-edit\",\"added\":42,\"managed\":\"external\"}");
        var second = store.Apply(Request(), first, true, _ => { });
        var actual = JsonNode.Parse(File.ReadAllText(ConfigPath))!;
        Assert.Equal("external-edit", actual["unmanaged"]!.GetValue<string>());
        Assert.Equal(42, actual["added"]!.GetValue<int>());
        Assert.Equal("new", actual["managed"]!.GetValue<string>());
        Assert.Equal(first.BackupPath, second.BackupPath);
        Assert.NotEqual(first.LastWrittenHash, second.LastWrittenHash);
    }

    [Fact]
    public void Apply_IdenticalContentPersistsMetadataWithoutReplacingFile()
    {
        Put(Original);
        _editor.WriteAction = (text, _) => text;
        var replacements = 0;
        var saves = 0;
        var before = File.GetLastWriteTimeUtc(ConfigPath);
        var store = Store(replace: (_, _, _) => replacements++);
        var state = store.Apply(Request(), new(), false, _ => saves++);
        var again = store.Apply(Request(), state, true, _ => saves++);
        Assert.Equal(0, replacements);
        Assert.Equal(2, saves);
        Assert.True(again.Enabled);
        Assert.Equal(before, File.GetLastWriteTimeUtc(ConfigPath));
        Assert.Equal(store.CurrentHash(), again.LastWrittenHash);
    }

    [Fact]
    public void Apply_ModelOnlyRequestReachesEditorAndKeepsOtherFields()
    {
        Put(Original);
        var request = new ClientConfigRequest
        {
            Channel = Channel(ClientType.Claude), ModelsOnly = true, UseProxy = true, ListenPort = 18081,
        };
        var state = Store().Apply(request, new(), true, _ => { });
        var actual = JsonNode.Parse(File.ReadAllText(ConfigPath))!;
        Assert.Equal("new", actual["model"]!.GetValue<string>());
        Assert.Equal("old", actual["managed"]!.GetValue<string>());
        Assert.Equal("redacted-user-setting", actual["unmanaged"]!.GetValue<string>());
        Assert.True(state.Enabled);
    }

    [Fact]
    public void Apply_EmptyExistingFileHasRealBackupAndIsRestoredAsEmpty()
    {
        Put(string.Empty);
        Assert.Throws<ClientConfigException>(() => Store().Apply(Request(), new(), true,
            _ => throw new IOException("redacted-secret")));
        Assert.True(File.Exists(ConfigPath));
        Assert.Empty(File.ReadAllBytes(ConfigPath));
        var file = Assert.Single(Directory.GetFiles(BackupRoot, "*", SearchOption.AllDirectories));
        Assert.Equal("settings.json", Path.GetFileName(file));
        Assert.Empty(File.ReadAllBytes(file));
    }

    [Fact]
    public void Apply_NoOpPersistFailureDoesNotRewriteClientFile()
    {
        Put(Original);
        _editor.WriteAction = (text, _) => text;
        var previous = new ClientTakeoverState { Enabled = false };
        var replacements = 0;
        var store = Store(replace: (_, _, _) => replacements++);
        var before = File.GetLastWriteTimeUtc(ConfigPath);
        Assert.Throws<ClientConfigException>(() => store.Apply(Request(), previous, true,
            _ => throw new IOException("redacted-secret")));
        Assert.Equal(0, replacements);
        Assert.False(previous.Enabled);
        Assert.Equal(before, File.GetLastWriteTimeUtc(ConfigPath));
        Assert.Equal(Original, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void Apply_IdenticalMissingContentDoesNotCreateClientFile()
    {
        _editor.WriteAction = (text, _) => text;
        var saves = 0;
        var result = Store().Apply(Request(), new(), false, _ => saves++);
        Assert.Equal(1, saves);
        Assert.Equal(string.Empty, result.LastWrittenHash);
        Assert.False(File.Exists(ConfigPath));
    }

    [Theory]
    [InlineData("utf8-bom")]
    [InlineData("utf16")]
    [InlineData("utf16-big")]
    public void Apply_PreservesEncodingAndRollbackRestoresExactBytes(string kind)
    {
        var encoding = kind switch
        {
            "utf16" => Encoding.Unicode,
            "utf16-big" => Encoding.BigEndianUnicode,
            _ => Encoding.UTF8,
        };
        Put(Original);
        byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes(Original)];
        File.WriteAllBytes(ConfigPath, bytes);
        var store = Store();
        Assert.Throws<ClientConfigException>(() => store.Apply(Request(), new(), true, _ =>
        {
            Assert.True(File.ReadAllBytes(ConfigPath).AsSpan().StartsWith(encoding.GetPreamble()));
            throw new InvalidOperationException("redacted-secret");
        }));
        Assert.Equal(bytes, File.ReadAllBytes(ConfigPath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(ConfigPath)!, "*.tmp"));
    }

    [Fact]
    public void Apply_PersistFailureDeletesFileThatOriginallyDidNotExist()
    {
        var previous = new ClientTakeoverState();
        var error = Assert.Throws<ClientConfigException>(() => Store().Apply(Request(), previous, true,
            _ => throw new IOException("redacted-secret")));
        Assert.False(File.Exists(ConfigPath));
        Assert.Equal(new ClientTakeoverState(), previous);
        Assert.Contains("已恢复", error.Message);
        Assert.DoesNotContain("redacted-secret", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void Apply_ReplacementUsesSiblingTempFileAndCleansItAfterSuccess()
    {
        Put(Original);
        var called = false;
        var store = Store(replace: (temporary, target, existed) =>
        {
            Assert.Equal(Path.GetDirectoryName(target), Path.GetDirectoryName(temporary));
            Assert.True(existed);
            Assert.Equal(Original, File.ReadAllText(target));
            Assert.True(File.Exists(temporary));
            called = true;
            File.Replace(temporary, target, null);
        });
        store.Apply(Request(), new(), true, _ => { });
        Assert.True(called);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(ConfigPath)!, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Apply_AtomicReplacementFailureRestoresOriginalAndDoesNotPersist(bool failAfterReplace)
    {
        Put(Original);
        var calls = 0;
        var store = Store(replace: (temporary, target, _) =>
        {
            calls++;
            if (failAfterReplace || calls > 1) File.Replace(temporary, target, null);
            if (calls == 1) throw new IOException("redacted-secret");
        });
        var persisted = false;
        var error = Assert.Throws<ClientConfigException>(() => store.Apply(Request(), new(), true, _ => persisted = true));
        Assert.False(persisted);
        Assert.Equal(Original, File.ReadAllText(ConfigPath));
        Assert.DoesNotContain("redacted-secret", error.ToString());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(ConfigPath)!, "*.tmp"));
    }

    [Fact]
    public void Apply_BackupFailureLeavesClientAndMetadataUntouched()
    {
        Put(Original);
        File.WriteAllText(BackupRoot, "redacted-backup-blocker");
        var persisted = false;
        Assert.Throws<ClientConfigException>(() => Store().Apply(Request(), new(), true, _ => persisted = true));
        Assert.False(persisted);
        Assert.Equal(Original, File.ReadAllText(ConfigPath));
    }

    [Theory]
    [InlineData("change")]
    [InlineData("delete")]
    [InlineData("create")]
    public void Apply_ConcurrentChangeIsRejectedAndNotOverwritten(string operation)
    {
        if (operation != "create") Put(Original);
        var store = Store(beforeWrite: () =>
        {
            if (operation == "delete") File.Delete(ConfigPath);
            else Put("{\"external\":\"redacted-concurrent-write\"}");
        });
        var persisted = false;
        Assert.Throws<ClientConfigException>(() => store.Apply(Request(), new(), true, _ => persisted = true));
        Assert.False(persisted);
        if (operation == "delete") Assert.False(File.Exists(ConfigPath));
        else Assert.Contains("redacted-concurrent-write", File.ReadAllText(ConfigPath));
        Assert.False(Directory.Exists(BackupRoot));
    }

    [Fact]
    public void Apply_RechecksHashAfterBackupBeforeReplacing()
    {
        Put(Original);
        var store = Store(clock: () =>
        {
            Put("{\"external\":true}");
            return new DateTime(2026, 9, 29);
        });
        var persisted = false;
        Assert.Throws<ClientConfigException>(() => store.Apply(Request(), new(), true, _ => persisted = true));
        Assert.False(persisted);
        Assert.Equal("{\"external\":true}", File.ReadAllText(ConfigPath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(ConfigPath)!, "*.tmp"));
    }

    [Fact]
    public void Apply_RollbackDoesNotClobberNewerExternalWrite()
    {
        Put(Original);
        var error = Assert.Throws<ClientConfigException>(() => Store().Apply(Request(), new(), true, _ =>
        {
            Put("{\"external\":true}");
            throw new IOException("redacted-secret");
        }));
        Assert.Equal("{\"external\":true}", File.ReadAllText(ConfigPath));
        Assert.Contains("无法自动恢复", error.Message);
        Assert.DoesNotContain("redacted-secret", error.ToString());
        var backup = Assert.Single(Directory.GetFiles(BackupRoot, "settings.json", SearchOption.AllDirectories));
        Assert.Equal(Original, File.ReadAllText(backup));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Apply_InjectedConfigBlocksTakeoverDirectAndModelOnlyWrites(bool useProxy, bool modelsOnly)
    {
        Put(Original);
        var request = new ClientConfigRequest { Channel = Channel(ClientType.Claude), UseProxy = useProxy, ModelsOnly = modelsOnly };
        var store = Store(writesBlocked: () => true);
        _editor.WriteAction = (_, _) => throw new Exception("编辑器不应被调用");
        var persisted = false;
        var error = Assert.Throws<ClientConfigException>(() => store.Apply(request, new(), useProxy, _ => persisted = true));
        Assert.Contains("注入配置", error.Message);
        Assert.Equal(Original, File.ReadAllText(ConfigPath));
        Assert.False(Directory.Exists(BackupRoot));
        Assert.False(persisted);
        store.ReadProfile(); // 只读不受禁写影响。
        Assert.NotEmpty(store.CurrentHash());
    }

    [Fact]
    public void Apply_RechecksWriteBlockAfterReading()
    {
        Put(Original);
        var blocked = false;
        var store = Store(beforeWrite: () => blocked = true, writesBlocked: () => blocked);
        Assert.Throws<ClientConfigException>(() => store.Apply(Request(), new(), true, _ => { }));
        Assert.Equal(Original, File.ReadAllText(ConfigPath));
        Assert.False(Directory.Exists(BackupRoot));
    }

    [Fact]
    public void PublicErrorsDoNotIncludeUnexpectedEditorMessagesOrPaths()
    {
        Put(Original);
        _editor.ReadAction = (_, _) => throw new IOException("redacted-secret-file-and-key");
        var readError = Assert.Throws<ClientConfigException>(() => Store().ReadProfile());
        _editor.WriteAction = (_, _) => throw new IOException("redacted-secret-file-and-key");
        var writeError = Assert.Throws<ClientConfigException>(() => Store().Apply(Request(), new(), true, _ => { }));
        var pathError = Assert.Throws<ClientConfigException>(() => new ClientConfigStore(BackupRoot, _editor, "redacted-secret\0"));
        foreach (var error in new[] { readError, writeError, pathError })
        {
            Assert.DoesNotContain("redacted-secret", error.ToString());
            Assert.Null(error.InnerException);
        }
    }

    [Fact]
    public void SafeValidationErrorsRemainActionable()
    {
        const string message = "目标键位于内联表，请改为独立配置项";
        _editor.WriteAction = (_, _) => throw new ClientConfigException(message);
        Assert.Equal(message, Assert.Throws<ClientConfigException>(() => Store().Apply(Request(), new(), true, _ => { })).Message);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void CodexWithProviderTokenDoesNotRequireReadableAuthFile()
    {
        Put(Original);
        _editor.ClientType = ClientType.Codex;
        _editor.Profile = new() { ClientType = ClientType.Codex, ApiKey = "test-provider-token" };
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(ConfigPath)!, "auth.json"));
        Assert.Equal("test-provider-token", Store().ReadProfile().ApiKey);
        Assert.Null(_editor.AuthText);
    }

    [Fact]
    public void ReadProfileAndHash_DoNotTreatAccessErrorsAsMissing()
    {
        Directory.CreateDirectory(ConfigPath);
        Assert.Throws<ClientConfigException>(() => Store().ReadProfile());
        Assert.Throws<ClientConfigException>(() => Store().CurrentHash());
    }

    private ClientConfigStore Store(Action<string, string, bool>? replace = null, Func<DateTime>? clock = null,
        Action? beforeWrite = null, Func<bool>? writesBlocked = null) =>
        new(BackupRoot, _editor, ConfigPath, replace, clock, beforeWrite, writesBlocked);

    private void Put(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, text, new UTF8Encoding(false));
    }

    private static int Port(ClientType client) => client == ClientType.Claude ? 18081 : 18080;
    private static ChannelSnapshot Channel(ClientType client) => new()
    {
        ClientType = client, LocalToken = "local-redacted", ApiKey = "provider-redacted",
        UpstreamBaseUrl = "https://supplier.invalid", Models = new() { Model = "redacted-model" },
    };
    private static ClientConfigRequest Request(ClientType client = ClientType.Claude) => new()
    {
        Channel = Channel(client), ListenPort = Port(client), UseProxy = true,
    };
    private static ClientProfile Profile(ClientType client, string url) => new()
    {
        ClientType = client, BaseUrl = url, ApiKey = "local-redacted", AuthMode = ClaudeAuthMode.Bearer, WireApi = "responses",
    };

    private sealed class FakeEditor : IClientConfigEditor
    {
        public ClientType ClientType { get; set; } = ClientType.Claude;
        public ClientProfile? Profile { get; set; }
        public string? ReadText { get; private set; }
        public string? AuthText { get; private set; }
        public Func<string, string?, ClientProfile>? ReadAction { get; set; }
        public Func<string, ClientConfigRequest, string>? WriteAction { get; set; }

        public ClientProfile Read(string text, string? authText = null)
        {
            ReadText = text;
            AuthText = authText;
            return ReadAction?.Invoke(text, authText) ?? Profile ?? new ClientProfile { ClientType = ClientType };
        }

        public string Write(string text, ClientConfigRequest request)
        {
            if (WriteAction is not null) return WriteAction(text, request);
            var value = string.IsNullOrEmpty(text) ? new JsonObject() : JsonNode.Parse(text)!.AsObject();
            value[request.ModelsOnly ? "model" : "managed"] = request.UseProxy ? "new" : "direct";
            return value.ToJsonString();
        }
    }
}
