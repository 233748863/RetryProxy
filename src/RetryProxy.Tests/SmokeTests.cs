using RetryProxy.Core.Config;
using Xunit;

namespace RetryProxy.Tests;

public class SmokeTests
{
    [Fact]
    public void ProxyConfig_DefaultPort_Is8080()
    {
        var config = new ProxyConfig();
        Assert.Equal(8080, config.BasePort);
        Assert.Empty(config.Channels);
    }
}
