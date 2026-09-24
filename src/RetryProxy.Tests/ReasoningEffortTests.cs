using System.Text.Json.Nodes;
using RetryProxy.Core.Config;
using Xunit;

namespace RetryProxy.Tests;

public sealed class ReasoningEffortTests
{
    [Fact]
    public void ChannelEffortsRoundTripAndRemainIndependentOfTheSelectedChannel()
    {
        var config = ProxyConfig.Builtin();
        config.Routes[0].KeepaliveReasoningEffort = ReasoningEffort.Ultra;
        config.Routes[1].KeepaliveReasoningEffort = ReasoningEffort.Max;
        config.Normalize();
        var saved = ProxyConfigJson.ToCanonicalJson(config);
        var restored = ProxyConfigJson.Parse(saved).Config;
        restored.Validate(true);

        Assert.Equal(ReasoningEffort.Ultra, restored.KeepaliveReasoningEffort);
        Assert.Equal(ReasoningEffort.Max, restored.RuntimeConfigFor(restored.Routes[1].Id).KeepaliveReasoningEffort);
        Assert.Equal(saved, ProxyConfigJson.ToCanonicalJson(restored));
        restored.SelectedRouteId = restored.Routes[1].Id;
        restored.Normalize();
        Assert.Equal(ReasoningEffort.Max, restored.KeepaliveReasoningEffort);
        Assert.Equal(ReasoningEffort.Ultra, restored.Routes[0].KeepaliveReasoningEffort);
    }

    [Fact]
    public void ExistingConfigurationsKeepClientDefaultsAndInvalidEffortsAreRejected()
    {
        var json = JsonNode.Parse(ProxyConfigJson.ToCanonicalJson(ProxyConfig.Builtin()))!;
        foreach (var route in json["routes"]!.AsArray())
        {
            route!.AsObject().Remove("keepalive_reasoning_effort");
        }
        var restored = ProxyConfigJson.Parse(json.ToJsonString()).Config;
        Assert.All(restored.Routes, route => Assert.Equal(ReasoningEffort.Default, route.KeepaliveReasoningEffort));

        json["routes"]![0]!["keepalive_reasoning_effort"] = "unknown";
        Assert.Throws<ConfigException>(() => ProxyConfigJson.Parse(json.ToJsonString()));
        restored.Routes[1].KeepaliveReasoningEffort = ReasoningEffort.Ultra;
        Assert.Throws<ConfigException>(() => restored.Validate(true));
    }
}
