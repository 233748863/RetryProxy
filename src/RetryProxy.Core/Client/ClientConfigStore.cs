using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using RetryProxy.Core.Cli;
using RetryProxy.Core.Config;

namespace RetryProxy.Core.Client;

/// <summary>客户端文件事务：备份原字节、同目录原子替换，状态保存失败时恢复原文件。</summary>
public sealed class ClientConfigStore
{
    /// <summary>原文件不存在时，备份目录里保存“原文件名 + 此后缀”的空标记文件。</summary>
    public const string MissingFileMarkerSuffix = ".missing";

    private readonly string _backupRoot;
    private readonly IClientConfigEditor _editor;
    private readonly Action<string, string, bool> _replace;
    private readonly Func<DateTime> _clock;
    private readonly Action? _beforeWrite;
    private readonly Func<bool>? _writesBlocked;
    private readonly Action<TimeSpan> _retryDelay;
    private readonly object _gate = new();
    private const int FileAccessRetries = 20;
    private static readonly TimeSpan FileAccessRetryDelay = TimeSpan.FromMilliseconds(25);

    public ClientConfigStore(string backupRoot, IClientConfigEditor editor, string configPath)
        : this(backupRoot, editor, configPath, null) { }

    /// <summary>测试只替换落盘边界；附加禁止条件只能收紧权限，不能绕过真实环境变量的保护。</summary>
    internal ClientConfigStore(string backupRoot, IClientConfigEditor editor, string configPath,
        Action<string, string, bool>? atomicReplace, Func<DateTime>? clock = null,
        Action? beforeWrite = null, Func<bool>? writesBlocked = null, Action<TimeSpan>? retryDelay = null)
    {
        try
        {
            if (editor is null || !Enum.IsDefined(editor.ClientType)
                || string.IsNullOrWhiteSpace(backupRoot) || string.IsNullOrWhiteSpace(configPath))
                throw new InvalidOperationException();
            _backupRoot = Path.GetFullPath(backupRoot);
            ConfigPath = Path.GetFullPath(configPath);
            _editor = editor;
            _replace = atomicReplace ?? Replace;
            _clock = clock ?? (() => DateTime.Now);
            _beforeWrite = beforeWrite;
            _writesBlocked = writesBlocked;
            _retryDelay = retryDelay ?? Thread.Sleep;
        }
        catch
        {
            throw new ClientConfigException("客户端配置存储参数无效，请检查配置目录。");
        }
    }

    public string ConfigPath { get; }

    public ClientProfile ReadProfile()
    {
        var stage = ClientConfigStage.ReadProfile;
        try
        {
            var file = ReadFile(ConfigPath);
            var profile = _editor.Read(file.Text);
            if (_editor.ClientType != ClientType.Codex || !string.IsNullOrWhiteSpace(profile.ApiKey)) return profile;
            stage = ClientConfigStage.ReadAuth;
            var auth = ReadFile(Path.Combine(Path.GetDirectoryName(ConfigPath)!, "auth.json"));
            return auth.Bytes is null ? profile : _editor.Read(file.Text, auth.Text);
        }
        catch (ClientConfigException error) { throw new ClientConfigException(error.Message, stage, error); }
        catch (Exception error)
        {
            // 不附带原异常：解析器错误、路径以及回调错误都可能包含密钥。
            throw new ClientConfigException("无法读取客户端配置，请检查文件格式和访问权限。", stage, error);
        }
    }

    public bool IsTakenOver(ChannelSnapshot channel, int listenPort)
    {
        if (channel is null || channel.ClientType != _editor.ClientType
            || string.IsNullOrEmpty(channel.LocalToken))
            return false;
        var profile = ReadProfile();
        return profile.ClientType == channel.ClientType
            && IsChannelUrl(profile.BaseUrl, channel.ClientType, listenPort)
            && string.Equals(profile.ApiKey, channel.LocalToken, StringComparison.Ordinal)
            && profile.AuthMode == ClaudeAuthMode.Bearer
            && !profile.HasApiKeyHelper && !profile.HasConflictingSettings
            && (channel.ClientType != ClientType.Codex || profile.WireApi == "responses");
    }

    public string CurrentHash()
    {
        try { return Hash(ReadBytes(ConfigPath)); }
        catch (Exception error) { throw new ClientConfigException("无法检查客户端配置，请检查文件访问权限。", ClientConfigStage.CheckHash, error); }
    }

    public ClientTakeoverState Apply(ClientConfigRequest request, ClientTakeoverState previous,
        bool enabled, Action<ClientTakeoverState> persist)
    {
        lock (_gate)
        {
            EnsureWritesAllowed();
            if (request?.Channel is null || request.Channel.ClientType != _editor.ClientType
                || previous is null || persist is null)
                throw new ClientConfigException("客户端配置写入参数无效。");

            FileContent? original = null;
            string? writtenHash = null;
            var replacementAttempted = false;
            var stage = ClientConfigStage.ReadOriginal;
            try
            {
                // 每次从磁盘重新合并，不能拿上次接管时的内容覆盖用户新增的其他键。
                original = ReadFile(ConfigPath);
                stage = ClientConfigStage.Edit;
                var text = _editor.Write(original.Text, request);
                var bytes = text == original.Text ? original.Bytes : original.Encode(text);
                writtenHash = Hash(bytes);
                var state = previous.Clone();
                state.Enabled = enabled;
                state.ConfigPath = ConfigPath;
                state.LastWrittenHash = writtenHash;

                stage = ClientConfigStage.VerifyOriginal;
                _beforeWrite?.Invoke();
                EnsureWritesAllowed();
                VerifyHash(original.Hash);
                // BackupPath 是目录。取消后重新接管仍沿用最初备份；改路径则为新文件另建备份。
                if (string.IsNullOrEmpty(previous.BackupPath) || !SamePath(previous.ConfigPath, ConfigPath))
                {
                    stage = ClientConfigStage.Backup;
                    state.BackupPath = Backup(original);
                }

                if (writtenHash != original.Hash)
                {
                    AtomicWrite(bytes!, original.Hash, () => replacementAttempted = true, value => stage = value);
                    stage = ClientConfigStage.VerifyWritten;
                    VerifyHash(writtenHash);
                }
                else
                {
                    stage = ClientConfigStage.VerifyOriginal;
                    VerifyHash(original.Hash);
                }

                // 即使内容没变，也要保存用户选择；回调修改传入对象不会改变返回值或 previous。
                stage = ClientConfigStage.PersistState;
                persist(state.Clone());
                return state;
            }
            catch (ClientConfigException error) when (!replacementAttempted)
            {
                // 编辑器已将解析器错误转成固定中文提示；保留内联表等可操作的拒写原因。
                throw new ClientConfigException(error.Message, stage, error);
            }
            catch (Exception error)
            {
                var failedStage = stage;
                if (replacementAttempted && original is not null && writtenHash is not null)
                {
                    try
                    {
                        stage = ClientConfigStage.RollbackRead;
                        var current = Hash(ReadBytes(ConfigPath));
                        if (current != original.Hash)
                        {
                            // 回滚也核对哈希，避免覆盖事务期间其他程序再次写入的内容。
                            stage = ClientConfigStage.RollbackVerify;
                            if (current != writtenHash)
                                throw new IOException();
                            if (original.Bytes is null)
                            {
                                RetryFileAccess(() =>
                                {
                                    stage = ClientConfigStage.RollbackVerify;
                                    EnsureWritesAllowed();
                                    VerifyHashOnce(writtenHash);
                                    stage = ClientConfigStage.RollbackDelete;
                                    File.Delete(ConfigPath);
                                });
                            }
                            else
                                AtomicWrite(original.Bytes, writtenHash, stageChanged: value => stage = value);
                        }
                    }
                    catch (Exception rollbackError)
                    {
                        throw new ClientConfigException("客户端配置保存失败，且无法自动恢复。原配置已备份，请检查文件权限或外部修改。",
                            failedStage, error, stage, rollbackError);
                    }
                    throw new ClientConfigException("客户端配置保存失败，已恢复写入前的文件。", failedStage, error);
                }
                throw new ClientConfigException("客户端配置未保存，请检查文件格式、访问权限或是否被其他程序修改。", failedStage, error);
            }
        }
    }

    /// <summary>只接受本客户端的完整通道地址；禁止子路径、查询串、用户信息和其他回环地址写法。</summary>
    internal static bool IsChannelUrl(string url, ClientType client, int port)
    {
        if (string.IsNullOrEmpty(url) || !Enum.IsDefined(client) || port is < 1 or > 65535)
            return false;
        var root = url.EndsWith('/') ? url[..^1] : url;
        if (client == ClientType.Codex)
        {
            if (!root.EndsWith("/v1", StringComparison.Ordinal))
                return false;
            root = root[..^3];
        }
        return string.Equals(root, $"http://127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase)
            || string.Equals(root, $"http://localhost:{port}", StringComparison.OrdinalIgnoreCase);
    }

    private void EnsureWritesAllowed()
    {
        if (ClientConfigPaths.WritesBlocked || _writesBlocked?.Invoke() == true)
            throw new ClientConfigException("当前使用注入配置，禁止写入客户端配置。");
    }

    private string Backup(FileContent original)
    {
        EnsureWritesAllowed();
        var directory = Path.Combine(_backupRoot, _editor.ClientType.AsStr(),
            $"{_clock().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var name = Path.GetFileName(ConfigPath) + (original.Bytes is null ? MissingFileMarkerSuffix : string.Empty);
        WriteNewFile(Path.Combine(directory, name), original.Bytes ?? []);
        return directory;
    }

    private void AtomicWrite(byte[] bytes, string expectedHash, Action? replacing = null,
        Action<ClientConfigStage>? stageChanged = null)
    {
        stageChanged?.Invoke(ClientConfigStage.WriteTemporary);
        EnsureWritesAllowed();
        var directory = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(ConfigPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            WriteNewFile(temporary, bytes);
            RetryFileAccess(() =>
            {
                // 例如客户端正在重读配置时暂缓替换；每次重试仍核对原字节，不能覆盖期间的外部修改。
                stageChanged?.Invoke(ClientConfigStage.VerifyBeforeReplace);
                EnsureWritesAllowed();
                VerifyHashOnce(expectedHash);
                stageChanged?.Invoke(ClientConfigStage.Replace);
                replacing?.Invoke();
                _replace(temporary, ConfigPath, expectedHash.Length != 0);
            }, retryReplaceFailure: expectedHash.Length != 0);
        }
        finally
        {
            // 清理失败不能盖掉保存/回滚的结果，也不能把带路径的原异常暴露出去。
            try { File.Delete(temporary); } catch { }
        }
    }

    private void VerifyHash(string expected) => RetryFileAccess(() => VerifyHashOnce(expected));

    private void VerifyHashOnce(string expected)
    {
        if (Hash(ReadBytesOnce(ConfigPath)) != expected)
            throw new IOException();
    }

    private void RetryFileAccess(Action operation, bool retryReplaceFailure = false) => RetryFileAccess(() =>
    {
        operation();
        return true;
    }, retryReplaceFailure);

    private T RetryFileAccess<T>(Func<T> operation, bool retryReplaceFailure = false)
    {
        for (var retry = 0; ; retry++)
        {
            try { return operation(); }
            catch (IOException error) when (retry < FileAccessRetries
                && ((error.HResult & 0xffff) is 32 or 33
                    || retryReplaceFailure && (error.HResult & 0xffff) == 1175))
            {
                // 替换时 1175 表示旧文件无法移除、两份文件仍保留；与共享/锁冲突一样累计等待 500 ms。
                // 不重试 1176/1177：它们可能已移动文件，必须走原有回滚；其他权限、磁盘与内容冲突也立即报错。
                _retryDelay(FileAccessRetryDelay);
            }
        }
    }

    private static void Replace(string temporary, string destination, bool exists)
    {
        if (exists) File.Replace(temporary, destination, null);
        else File.Move(temporary, destination); // 不覆盖：读取时不存在而现在已创建，也视作竞争。
    }

    private static void WriteNewFile(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static bool SamePath(string first, string second) => !string.IsNullOrEmpty(first)
        && string.Equals(Path.GetFullPath(first), second, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string Hash(byte[]? bytes) => bytes is null ? string.Empty : Convert.ToHexString(SHA256.HashData(bytes));

    private byte[]? ReadBytes(string path) => RetryFileAccess(() => ReadBytesOnce(path));

    private static byte[]? ReadBytesOnce(string path)
    {
        // 允许其他程序原子替换文件，但不允许同时原地改写；打开的句柄始终读取同一份完整内容。
        // File.Exists 会把无权限等错误当作不存在；只把确实缺文件的情况按空内容处理。
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private FileContent ReadFile(string path) => new(ReadBytes(path));

    private sealed class FileContent
    {
        public byte[]? Bytes { get; }
        public string Text { get; }
        public string Hash { get; }
        private readonly Encoding _encoding;
        private readonly byte[] _preamble;

        public FileContent(byte[]? bytes)
        {
            Bytes = bytes;
            Hash = ClientConfigStore.Hash(bytes);
            using var reader = new StreamReader(new MemoryStream(bytes ?? []),
                new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            Text = reader.ReadToEnd();
            _encoding = reader.CurrentEncoding;
            var preamble = _encoding.GetPreamble();
            _preamble = bytes is not null && bytes.AsSpan().StartsWith(preamble) ? preamble : [];
        }

        public byte[] Encode(string text) => [.. _preamble, .. _encoding.GetBytes(text)];
    }
}
