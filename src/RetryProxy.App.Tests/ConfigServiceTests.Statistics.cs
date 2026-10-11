using System;
using RetryProxy.Core.Metrics;
using Xunit;

namespace RetryProxy.App.Tests;

public sealed partial class ConfigServiceTests
{
    [Fact]
    public void ModelPricesCatalogAndContextTiersSurviveSqliteRestart()
    {
        Seed(Rows(Sample()));
        var service = Open();
        var price = new ModelPrice
        {
            Rates = new TokenPrices { Input = 2, Output = 10, CacheRead = 0, CacheWrite = 2.5m, CacheWrite1h = 4 },
            Tiers = [new ModelPriceTier { AboveTokens = 272000, Rates = new TokenPrices { Input = 4, Output = 15, CacheRead = 0.4m } }],
        };
        service.Get().OtherConfig.StatisticsModelPrices =
        [
            new ModelPriceOverride { Model = "custom-model", Price = price },
            new ModelPriceOverride { Model = "custom-model", ProviderId = "provider-1", Price = price with { Rates = price.Rates with { Input = 0 } } },
        ];
        service.Get().OtherConfig.StatisticsPublicPrices = new PublicPriceCatalog
        {
            UpdatedAt = DateTimeOffset.Parse("2026-10-11T00:00:00Z"),
            Entries = [new ModelPriceEntry { Model = "published-model", Provider = "openai", Price = price }],
        };
        service.SaveChecked();
        service.Dispose();
        var restored = Open().Get().OtherConfig;
        Assert.Equal(2, restored.StatisticsModelPrices.Count);
        var prices = new ModelPricing(restored.StatisticsPublicPrices, restored.StatisticsModelPrices);
        var specific = prices.Find("provider-1", "custom-model");
        Assert.Equal("provider_override", specific.Source);
        Assert.Equal(0, specific.Price!.Rates.Input);
        Assert.Equal(0, specific.Price.Rates.CacheRead);
        Assert.Equal(4, specific.Price.Rates.CacheWrite1h);
        Assert.Equal(15, specific.Price.At(272001).Output);
        Assert.Equal(2, prices.Find("provider-2", "custom-model").Price!.Rates.Input);
        Assert.Equal(10, prices.Find("provider-2", "published-model").Price!.At(272000).Output);
        Assert.Equal(2026, prices.Catalog.UpdatedAt.Year);
    }
}
