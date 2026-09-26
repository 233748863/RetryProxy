using System;
using System.IO;
using RetryProxy.Core.Cli;
using RetryProxy.Core.Workspace;
using Xunit;

namespace RetryProxy.Tests;

public class LocalProviderCredentialsTests
{
    [Fact]
    public void CodexReadsCurrentProviderAndKeyAfreshAfterSwitch()
    {
        var directory = Path.Combine(Path.GetTempPath(), "retry-proxy-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "config.toml"), """
                model_provider = "first"
                [model_providers.first]
                base_url = "https://first.example/v1"
                [model_providers."second"]
                base_url = 'https://second.example/v1'
                [mcp_servers.example]
                env_key = "IGNORED"
                """);
            File.WriteAllText(Path.Combine(directory, "auth.json"), """{"OPENAI_API_KEY":"sk-first"}""");
            var first = LocalProviderCredentials.ReadCodex(directory);
            Assert.Equal("https://first.example/v1", first.BaseUrl);
            Assert.Equal("sk-first", first.ApiKey);
            Assert.DoesNotContain("sk-first", first.ToString());

            File.WriteAllText(Path.Combine(directory, "config.toml"), """
                model_provider = "second"
                [model_providers.first]
                base_url = "https://first.example/v1"
                [model_providers."second"]
                base_url = 'https://second.example/v1'
                """);
            File.WriteAllText(Path.Combine(directory, "auth.json"), """{"OPENAI_API_KEY":"sk-second"}""");
            var second = LocalProviderCredentials.ReadCodex(directory);
            Assert.Equal("https://second.example/v1", second.BaseUrl);
            Assert.Equal("sk-second", second.ApiKey);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InvalidCodexConfigurationFailsWithoutDisclosingSecret()
    {
        var directory = Path.Combine(Path.GetTempPath(), "retry-proxy-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "config.toml"), "model_provider = 'missing'");
            File.WriteAllText(Path.Combine(directory, "auth.json"), """{"OPENAI_API_KEY":"sk-private"}""");
            var error = Assert.Throws<WorkspaceException>(() => LocalProviderCredentials.ReadCodex(directory));
            Assert.DoesNotContain("sk-private", error.Message);
            Assert.Contains("供应商", error.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClaudeReadsCurrentAddressAndToken()
    {
        var directory = Path.Combine(Path.GetTempPath(), "retry-proxy-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "settings.json"),
                """{"env":{"ANTHROPIC_BASE_URL":"https://claude.example","ANTHROPIC_AUTH_TOKEN":"sk-claude"}}""");
            var credential = LocalProviderCredentials.ReadClaude(directory);
            Assert.Equal("https://claude.example", credential.BaseUrl);
            Assert.Equal("sk-claude", credential.ApiKey);
            Assert.Equal(ClaudeAuthMode.Bearer, credential.AuthMode);

            File.WriteAllText(Path.Combine(directory, "settings.json"),
                """{"env":{"ANTHROPIC_BASE_URL":"https://claude.example","ANTHROPIC_API_KEY":"sk-api-key"}}""");
            credential = LocalProviderCredentials.ReadClaude(directory);
            Assert.Equal("sk-api-key", credential.ApiKey);
            Assert.Equal(ClaudeAuthMode.ApiKey, credential.AuthMode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
