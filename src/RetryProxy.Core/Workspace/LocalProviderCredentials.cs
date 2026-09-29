using System.IO;
using RetryProxy.Core.Cli;
using RetryProxy.Core.Client;
using RetryProxy.Core.Config;

namespace RetryProxy.Core.Workspace;

internal static class LocalProviderCredentials
{
    public static CliCredential Read(ClientType clientType) => ReadFile(clientType, ClientConfigPaths.Resolve(clientType));

    internal static CliCredential ReadCodex(string directory) => ReadFile(ClientType.Codex, Path.Combine(directory, "config.toml"));

    internal static CliCredential ReadClaude(string directory) => ReadFile(ClientType.Claude, Path.Combine(directory, "settings.json"));

    private static CliCredential ReadFile(ClientType client, string path)
    {
        try
        {
            IClientConfigEditor editor = client == ClientType.Claude ? new ClaudeConfigEditor() : new CodexConfigEditor();
            var profile = new ClientConfigStore(Path.Combine(Path.GetDirectoryName(path)!, "backup"), editor, path).ReadProfile();
            if (profile.HasConflictingSettings)
                throw new WorkspaceException("客户端当前供应商存在其他认证或配置覆盖，请检查客户端配置");
            if (string.IsNullOrWhiteSpace(profile.BaseUrl) || string.IsNullOrWhiteSpace(profile.ApiKey))
                throw new WorkspaceException("客户端当前供应商缺少地址或 API Key，请检查客户端配置");
            var provider = new ProviderEndpoint("本机当前供应商", profile.BaseUrl);
            provider.Validate();
            return CliCredential.Create(profile.ApiKey, provider.BaseUrl, authMode: profile.AuthMode);
        }
        catch (ClientConfigException)
        {
            throw new WorkspaceException("无法读取客户端当前供应商，请检查配置文件格式和权限");
        }
        catch (ConfigException)
        {
            throw new WorkspaceException("客户端当前供应商地址无效，请检查客户端配置");
        }
        catch (CliException)
        {
            throw new WorkspaceException("客户端当前供应商密钥无效，请检查客户端配置");
        }
    }
}
