using System;
using System.Collections.Generic;
using System.IO;
using RetryProxy.Core.Client;
using RetryProxy.Core.Config;
using Xunit;

namespace RetryProxy.Tests;

public sealed class ClientConfigPathsTests
{
    [Theory]
    [InlineData(ClientType.Claude, "CLAUDE_CONFIG_DIR", "settings.json")]
    [InlineData(ClientType.Codex, "CODEX_HOME", "config.toml")]
    public void Resolve_UsesClientSpecificEnvironment(ClientType client, string variable, string filename)
    {
        var directory = Path.Combine(Path.GetTempPath(), "脱敏目录", "client");
        var environment = new Dictionary<string, string?> { [variable] = directory };
        var result = ClientConfigPaths.Resolve(client, key => environment.GetValueOrDefault(key), "unused-home");
        Assert.Equal(Path.GetFullPath(Path.Combine(directory, filename)), result);
    }

    [Theory]
    [InlineData(ClientType.Claude, ".claude", "settings.json", null)]
    [InlineData(ClientType.Codex, ".codex", "config.toml", "")]
    [InlineData(ClientType.Claude, ".claude", "settings.json", " ")]
    public void Resolve_DefaultsToProvidedHome(ClientType client, string directory, string filename, string? value)
    {
        var home = Path.Combine(Path.GetTempPath(), "redacted-home");
        Assert.Equal(Path.Combine(home, directory, filename), ClientConfigPaths.Resolve(client, _ => value, home));
    }

    [Fact]
    public void Resolve_ReturnsAbsolutePathForRelativeEnvironment()
    {
        var result = ClientConfigPaths.Resolve(ClientType.Codex, _ => "redacted-relative-config", "unused");
        Assert.True(Path.IsPathFullyQualified(result));
        Assert.EndsWith(Path.Combine("redacted-relative-config", "config.toml"), result);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", true)]
    [InlineData(" ", true)]
    [InlineData("{}", true)]
    [InlineData("invalid-injected-config", true)]
    public void WritesBlocked_DependsOnPresenceWithoutChangingProcessEnvironment(string? value, bool expected)
    {
        Assert.Equal(expected, ClientConfigPaths.HasInjectedConfig(key =>
        {
            Assert.Equal("RETRY_PROXY_CONFIG_JSON", key);
            return value;
        }));
    }

    [Fact]
    public void Resolve_InvalidDirectoryAndClientUseSafeErrors()
    {
        var error = Assert.Throws<ClientConfigException>(() => ClientConfigPaths.Resolve(
            ClientType.Claude, _ => "redacted-secret\0", "unused"));
        Assert.DoesNotContain("redacted-secret", error.ToString());
        Assert.Null(error.InnerException);
        Assert.Throws<ClientConfigException>(() => ClientConfigPaths.Resolve((ClientType)999, _ => null, "unused"));
        Assert.Throws<ClientConfigException>(() => ClientConfigPaths.Resolve(ClientType.Claude, _ => null, ""));
    }
}
