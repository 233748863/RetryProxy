using System;
using System.Linq;
using RetryProxy.Core.Cli;
using RetryProxy.Core.Client;
using RetryProxy.Core.Config;
using Xunit;

namespace RetryProxy.Tests;

public sealed class ClientImportTests
{
    [Theory]
    [InlineData(ClientType.Claude, ClaudeAuthMode.Bearer)]
    [InlineData(ClientType.Claude, ClaudeAuthMode.ApiKey)]
    [InlineData(ClientType.Codex, ClaudeAuthMode.ApiKey)]
    public void ExternalPreviewAndDraftPreserveKeyModelsAndAuth(ClientType client, ClaudeAuthMode auth)
    {
        var profile = Profile(client, "https://supplier.invalid/custom/", auth: auth);
        var config = Config();
        var preview = ClientImport.Preview(profile, config, client);
        Assert.Equal(ClientImportKind.External, preview.Kind);
        Assert.True(preview.CanImport);
        Assert.Equal("supplier.invalid", preview.SuggestedName);
        Assert.Equal(2, config.Providers.Count);
        var provider = ClientImport.CreateProvider(profile, client, " 导入供应商 ");
        Assert.True(Guid.TryParseExact(provider.Id, "N", out _));
        Assert.Equal(client, provider.ClientType);
        Assert.Equal("导入供应商", provider.Name);
        Assert.Equal("https://supplier.invalid/custom", provider.BaseUrl);
        Assert.Equal(client == ClientType.Codex ? ClaudeAuthMode.Bearer : auth, provider.AuthMode);
        Assert.Equal(profile.Models, provider.Models);
        Assert.NotSame(profile.Models, provider.Models);
        Assert.NotSame(profile.Models.Opus, provider.Models.Opus);
        var key = Assert.Single(provider.Keys);
        Assert.True(Guid.TryParseExact(key.Id, "N", out _));
        Assert.NotEqual(provider.Id, key.Id);
        Assert.Equal("默认", key.Name);
        Assert.Equal("redacted-real-key", key.ApiKey);
        provider.Models.Opus.Model = "modified-draft";
        Assert.Equal("redacted-opus", profile.Models.Opus.Model);
        Assert.DoesNotContain(profile.ApiKey, preview.ToString());
    }

    [Fact]
    public void ConflictingClientSettingsAreNotImportedAsEffectiveCredentials()
    {
        var profile = new ClientProfile { ClientType = ClientType.Codex, BaseUrl = "https://supplier.invalid",
            ApiKey = "test-key", HasConflictingSettings = true };
        var preview = ClientImport.Preview(profile, Config(), ClientType.Codex);
        Assert.Equal(ClientImportKind.Unsupported, preview.Kind);
        Assert.False(preview.CanImport);
        Assert.Throws<ClientConfigException>(() => ClientImport.CreateProvider(profile, ClientType.Codex, "test"));
    }

    [Fact]
    public void Preview_SuggestedNamesAreUniqueWithinClientOnly()
    {
        var config = Config();
        config.Providers[0].Name = "supplier.invalid";
        var profile = Profile(ClientType.Claude, "https://supplier.invalid");
        Assert.Equal("supplier.invalid", ClientImport.Preview(profile, config, ClientType.Claude).SuggestedName);
        config.Providers[1].Name = "supplier.invalid";
        Assert.Equal("supplier.invalid (2)", ClientImport.Preview(profile, config, ClientType.Claude).SuggestedName);
    }

    [Theory]
    [InlineData(ClientType.Claude, "http://127.0.0.1:18081")]
    [InlineData(ClientType.Claude, "http://localhost:18081/")]
    [InlineData(ClientType.Codex, "http://127.0.0.1:18080/v1")]
    [InlineData(ClientType.Codex, "http://LOCALHOST:18080/v1/")]
    [InlineData(ClientType.Claude, "https://127.0.0.1:18081")]
    [InlineData(ClientType.Claude, "http://localhost:18081/other")]
    [InlineData(ClientType.Codex, "http://localhost:18080")]
    [InlineData(ClientType.Codex, "http://localhost:18080/V1")]
    [InlineData(ClientType.Codex, "http://localhost:18080/v1wrong")]
    public void ExistingChannel_OffersRealKeyForCurrentProvider(ClientType client, string url)
    {
        var config = Config();
        var preview = ClientImport.Preview(Profile(client, url), config, client);
        Assert.Equal(ClientImportKind.ExistingChannel, preview.Kind);
        Assert.True(preview.CanImport);
        Assert.Equal($"route-{client}", preview.ExistingRouteId);
        Assert.Equal($"provider-{client}", preview.ExistingProviderId);
        Assert.Equal("默认", preview.SuggestedName);
        Assert.All(config.Providers, provider => Assert.Empty(provider.Keys));
        Assert.Throws<ClientConfigException>(() => ClientImport.CreateProvider(Profile(client, url), client, "test"));
    }

    [Theory]
    [InlineData("local-claude-redacted")]
    [InlineData("local-codex-redacted")]
    public void ExistingChannel_LocalTokensAreNeverImported(string token)
    {
        var preview = ClientImport.Preview(Profile(ClientType.Claude, "http://127.0.0.1:18081", token), Config(), ClientType.Claude);
        Assert.Equal(ClientImportKind.ExistingChannel, preview.Kind);
        Assert.False(preview.CanImport);
        Assert.Contains("本地口令", preview.Message);
        Assert.DoesNotContain(token, preview.ToString());
    }

    [Fact]
    public void ExternalAddressWithLocalTokenIsUnsupported()
    {
        var preview = ClientImport.Preview(Profile(ClientType.Claude, "https://supplier.invalid", "local-claude-redacted"), Config(), ClientType.Claude);
        Assert.Equal(ClientImportKind.Unsupported, preview.Kind);
        Assert.False(preview.CanImport);
    }

    [Fact]
    public void ExistingChannel_DuplicateKeyIsReportedWithoutMutation()
    {
        var config = Config();
        var provider = config.Providers.Single(candidate => candidate.ClientType == ClientType.Claude);
        provider.Keys.Add(new ProviderKey { Id = "existing-key", Name = "已保存", ApiKey = "redacted-real-key" });
        var preview = ClientImport.Preview(Profile(ClientType.Claude, "http://127.0.0.1:18081"), config, ClientType.Claude);
        Assert.Equal(ClientImportKind.ExistingChannel, preview.Kind);
        Assert.True(preview.KeyAlreadyExists);
        Assert.False(preview.CanImport);
        Assert.Single(provider.Keys);
    }

    [Fact]
    public void ExistingChannel_WithoutCurrentProviderCannotImport()
    {
        var config = Config();
        config.Providers.Clear();
        var preview = ClientImport.Preview(Profile(ClientType.Claude, "http://127.0.0.1:18081"), config, ClientType.Claude);
        Assert.Equal(ClientImportKind.ExistingChannel, preview.Kind);
        Assert.False(preview.CanImport);
        Assert.Empty(preview.ExistingProviderId);
    }

    [Theory]
    [InlineData(ClientType.Claude, "http://127.0.0.1:29999")]
    [InlineData(ClientType.Claude, "http://localhost:29999")]
    [InlineData(ClientType.Claude, "http://127.0.0.1:18080")]
    [InlineData(ClientType.Claude, "http://127.1:18081")]
    [InlineData(ClientType.Claude, "http://[::1]:18081")]
    [InlineData(ClientType.Claude, "http://127.0.0.2:18081")]
    [InlineData(ClientType.Codex, "http://localhost:18081")]
    [InlineData(ClientType.Codex, "http://[::1]:18080/v1")]
    public void OtherLocalProxy_IsNotConfusedWithKnownChannel(ClientType client, string url)
    {
        var preview = ClientImport.Preview(Profile(client, url), Config(), client);
        Assert.Equal(ClientImportKind.OtherLocalProxy, preview.Kind);
        Assert.False(preview.CanImport);
        Assert.Empty(preview.ExistingProviderId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("file:///redacted-config")]
    [InlineData("https://user:password@supplier.invalid")]
    [InlineData("https://supplier.invalid?key=redacted")]
    [InlineData("https://supplier.invalid#key")]
    [InlineData("https://supplier.invalid/path with spaces")]
    [InlineData("https://supplier.invalid\r\n")]
    [InlineData("http://user:password@localhost:18081")]
    [InlineData("http://127.0.0.1:18081?key=redacted")]
    [InlineData("http://127.0.0.1:18081#fragment")]
    public void InvalidAddressesAreUnsupportedAndCannotCreateProvider(string url)
    {
        var profile = Profile(ClientType.Claude, url);
        var result = ClientImport.Preview(profile, Config(), ClientType.Claude);
        Assert.Equal(ClientImportKind.Unsupported, result.Kind);
        Assert.False(result.CanImport);
        Assert.Throws<ClientConfigException>(() => ClientImport.CreateProvider(profile, ClientType.Claude, "测试供应商"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("redacted key")]
    [InlineData("redacted\tkey")]
    [InlineData("redacted\nkey")]
    [InlineData("redacted\0key")]
    public void InvalidOrMissingKeysAreUnsupported(string key)
    {
        var profile = Profile(ClientType.Codex, "https://supplier.invalid", key);
        var preview = ClientImport.Preview(profile, Config(), ClientType.Codex);
        Assert.Equal(ClientImportKind.Unsupported, preview.Kind);
        Assert.False(preview.CanImport);
        Assert.Throws<ClientConfigException>(() => ClientImport.CreateProvider(profile, ClientType.Codex, "测试"));
    }

    [Fact]
    public void OfficialOAuthProfileIsSkippedAndCodexGroupingWarningIsAvailable()
    {
        var profile = new ClientProfile { ClientType = ClientType.Codex, ProviderName = "openai" };
        var preview = ClientImport.Preview(profile, Config(), ClientType.Codex);
        Assert.Equal(ClientImportKind.Unsupported, preview.Kind);
        Assert.False(preview.CanImport);
        Assert.True(preview.RequiresProviderRename);
        Assert.Contains("官方登录不支持", preview.Message);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("openai", true)]
    [InlineData("existing-provider", false)]
    public void CodexGroupingWarningDependsOnExistingProviderName(string name, bool expected)
    {
        var profile = new ClientProfile
        {
            ClientType = ClientType.Codex, BaseUrl = "https://supplier.invalid", ApiKey = "redacted-key", ProviderName = name,
        };
        Assert.Equal(expected, ClientImport.Preview(profile, Config(), ClientType.Codex).RequiresProviderRename);
    }

    [Fact]
    public void ProfileClientMismatchAndInvalidModelsAreRejected()
    {
        var profile = Profile(ClientType.Claude, "https://supplier.invalid");
        Assert.False(ClientImport.Preview(profile, Config(), ClientType.Codex).CanImport);
        Assert.Throws<ClientConfigException>(() => ClientImport.CreateProvider(profile, ClientType.Codex, "test"));
        profile.Models.ContextWindow = 10;
        profile.Models.AutoCompactTokenLimit = 11;
        Assert.False(ClientImport.Preview(profile, Config(), ClientType.Claude).CanImport);
        Assert.Throws<ClientConfigException>(() => ClientImport.CreateProvider(profile, ClientType.Claude, "test"));
    }

    [Fact]
    public void CreateProvider_UsesFreshIdsAndSafeValidationErrors()
    {
        var profile = Profile(ClientType.Claude, "https://supplier.invalid");
        var first = ClientImport.CreateProvider(profile, ClientType.Claude, "test");
        var second = ClientImport.CreateProvider(profile, ClientType.Claude, "test");
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.Keys[0].Id, second.Keys[0].Id);
        var error = Assert.Throws<ClientConfigException>(() => ClientImport.CreateProvider(profile, ClientType.Claude, " "));
        Assert.DoesNotContain(profile.ApiKey, error.ToString());
        Assert.DoesNotContain(profile.BaseUrl, error.ToString());
        Assert.Null(error.InnerException);
    }

    private static ClientProfile Profile(ClientType client, string url, string key = "redacted-real-key", ClaudeAuthMode auth = ClaudeAuthMode.Bearer) => new()
    {
        ClientType = client, BaseUrl = url, ApiKey = key, AuthMode = auth,
        ProviderName = "existing-provider", WireApi = "responses",
        Models = new ProviderModels
        {
            Model = "redacted-model", Context1M = true, Opus = new() { Model = "redacted-opus", Context1M = true },
            ContextWindow = 100_000, AutoCompactTokenLimit = 80_000,
        },
    };

    private static ProxyConfig Config() => new()
    {
        Providers =
        [
            new ProviderEndpoint { Id = $"provider-{ClientType.Codex}", ClientType = ClientType.Codex, Name = "测试Codex", BaseUrl = "https://codex.invalid" },
            new ProviderEndpoint { Id = $"provider-{ClientType.Claude}", ClientType = ClientType.Claude, Name = "测试Claude", BaseUrl = "https://claude.invalid" },
        ],
        Routes =
        [
            new ProxyRoute
            {
                Id = $"route-{ClientType.Codex}", ClientType = ClientType.Codex, ListenPort = 18080,
                CurrentProviderId = $"provider-{ClientType.Codex}", LocalToken = "local-codex-redacted",
            },
            new ProxyRoute
            {
                Id = $"route-{ClientType.Claude}", ClientType = ClientType.Claude, ListenPort = 18081,
                CurrentProviderId = $"provider-{ClientType.Claude}", LocalToken = "local-claude-redacted",
            },
        ],
    };
}
