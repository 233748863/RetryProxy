using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RetryProxy.Core.Metrics;

/// <summary>美元 / 百万 token；null 表示没有公布价格，0 表示明确免费。</summary>
public sealed record TokenPrices
{
    public decimal? Input { get; init; }
    public decimal? Output { get; init; }
    public decimal? CacheRead { get; init; }
    public decimal? CacheWrite { get; init; }
    public decimal? CacheWrite1h { get; init; }

    public bool IsValid => new[] { Input, Output, CacheRead, CacheWrite, CacheWrite1h }.All(value => value is null or >= 0);
}

public sealed record ModelPriceTier
{
    public ulong AboveTokens { get; init; }
    public TokenPrices Rates { get; init; } = new();
}

public sealed record ModelPrice
{
    public TokenPrices Rates { get; init; } = new();
    public List<ModelPriceTier> Tiers { get; init; } = new();

    public bool IsValid => Rates is { IsValid: true } && Tiers is not null
        && Tiers.All(t => t is { AboveTokens: > 0, Rates.IsValid: true })
        && Tiers.Select(t => t.AboveTokens).Distinct().Count() == Tiers.Count;

    public TokenPrices At(decimal prompt)
    {
        var rates = Rates;
        foreach (var tier in Tiers.OrderBy(t => t.AboveTokens))
            if (prompt > tier.AboveTokens) rates = tier.Rates;
        return rates;
    }

    public PriceQuote Quote(RequestUsage? usage, string source)
    {
        if (usage is null || !IsValid) return new(null, false, source);
        var n = usage.Normalize();
        if (n.Invalid || n.Input is null || n.Output is null || n.CacheRead is null || n.Prompt is null)
            return new(null, false, source);
        // 当缓存写入缺失且可能跨价格档位时，不能声称下面的金额是可靠下限。
        if (n.PromptIsLowerBound && Tiers.Any(t => t.AboveTokens >= n.Prompt)) return new(null, false, source);
        var rates = At(n.Prompt.Value);
        var oneHour = (decimal)(usage.CacheCreation1hTokens ?? 0);
        if (oneHour > (n.CacheWrite ?? 0)) return new(null, false, source);
        try
        {
            var amount = Cost(n.Input.Value, rates.Input) + Cost(n.Output.Value, rates.Output)
                + Cost(n.CacheRead.Value, rates.CacheRead)
                + Cost((n.CacheWrite ?? 0) - oneHour, rates.CacheWrite)
                + Cost(oneHour, rates.CacheWrite1h);
            return new(amount, n.PromptIsLowerBound, source);
        }
        catch (OverflowException) { return new(null, false, source); }
    }

    private static decimal? Cost(decimal tokens, decimal? price) => tokens == 0 ? 0 : tokens * price / 1_000_000m;
}

public sealed record PriceQuote(decimal? Amount, bool IsLowerBound, string Source);

public sealed record ModelPriceEntry
{
    public string Provider { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string CanonicalModel { get; init; } = string.Empty;
    public ModelPrice Price { get; init; } = new();
}

public sealed record PublicPriceCatalog
{
    public DateTimeOffset UpdatedAt { get; init; }
    public List<ModelPriceEntry> Entries { get; init; } = new();
}

public sealed record ModelPriceOverride
{
    /// <summary>空字符串为全部供应商，否则只对该供应商生效。</summary>
    public string ProviderId { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public ModelPrice Price { get; init; } = new();
}

/// <summary>只用原厂目录的公开价，避免把中转商折扣误标成模型公开价。不猜测未知模型。</summary>
public sealed class ModelPricing
{
    public const string SourceUrl = "https://models.dev/api.json";
    public static IReadOnlyList<string> OfficialProviders { get; } = new[]
    {
        "openai", "anthropic", "google", "deepseek", "moonshotai", "zai", "minimax", "xai",
        "mistral", "alibaba", "qwen", "cohere", "ai21", "perplexity", "stepfun", "nvidia",
    };

    private static readonly Lazy<PublicPriceCatalog> Bundled = new(LoadBundled);
    private readonly Dictionary<string, ModelPriceEntry> _public = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModelPrice> _overrides = new(StringComparer.OrdinalIgnoreCase);

    public PublicPriceCatalog Catalog { get; }
    public IEnumerable<string> Models => Catalog.Entries.Select(e => e.Model).Distinct(StringComparer.OrdinalIgnoreCase);

    public ModelPricing(PublicPriceCatalog? catalog = null, IEnumerable<ModelPriceOverride>? overrides = null)
    {
        Catalog = catalog is { Entries.Count: > 0 } ? catalog : Bundled.Value;
        foreach (var entry in Catalog.Entries.Where(e => e is { Price.IsValid: true } && !string.IsNullOrWhiteSpace(e.Model)))
        {
            _public.TryAdd(entry.Model, entry);
            _public.TryAdd(entry.Provider + "/" + entry.Model, entry);
            if (!string.IsNullOrWhiteSpace(entry.CanonicalModel)) _public.TryAdd(entry.CanonicalModel, entry);
        }
        foreach (var entry in overrides ?? [])
            if (entry is { Price.IsValid: true } && !string.IsNullOrWhiteSpace(entry.Model))
                _overrides[OverrideKey(entry.ProviderId, entry.Model)] = entry.Price;
    }

    public static string OverrideKey(string providerId, string model) => providerId + "\n" + model.Trim();

    public (ModelPrice? Price, string Source) Find(string providerId, string model)
    {
        if (_overrides.TryGetValue(OverrideKey(providerId, model), out var specific)) return (specific, "provider_override");
        if (_overrides.TryGetValue(OverrideKey(string.Empty, model), out var general)) return (general, "model_override");
        return _public.TryGetValue(model.Trim(), out var entry) ? (entry.Price, "models.dev / " + entry.Provider) : (null, string.Empty);
    }

    public PriceQuote Quote(string providerId, string model, RequestUsage? usage)
    {
        var (price, source) = Find(providerId, model);
        return price?.Quote(usage, source) ?? new(null, false, string.Empty);
    }

    private static PublicPriceCatalog LoadBundled()
    {
        using var stream = typeof(ModelPricing).Assembly.GetManifestResourceStream("RetryProxy.Core.Metrics.model-prices.json")
            ?? throw new InvalidDataException("内置模型价目表缺失");
        using var reader = new StreamReader(stream);
        return ParseModelsDev(reader.ReadToEnd(), new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero));
    }

    public static PublicPriceCatalog ParseModelsDev(string json, DateTimeOffset updatedAt)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("模型价目表格式不正确");
        var entries = new List<ModelPriceEntry>();
        foreach (var provider in OfficialProviders)
        {
            if (!document.RootElement.TryGetProperty(provider, out var vendor)
                || vendor.ValueKind != JsonValueKind.Object || !vendor.TryGetProperty("models", out var models)
                || models.ValueKind != JsonValueKind.Object) continue;
            foreach (var item in models.EnumerateObject())
            {
                var model = item.Value;
                if (model.ValueKind != JsonValueKind.Object || !model.TryGetProperty("cost", out var cost)
                    || cost.ValueKind != JsonValueKind.Object) continue;
                var rates = ReadRates(cost, provider);
                if (rates.Input is null || rates.Output is null || !rates.IsValid) continue;
                var tiers = new List<ModelPriceTier>();
                if (cost.TryGetProperty("tiers", out var tiersJson) && tiersJson.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tier in tiersJson.EnumerateArray())
                    {
                        if (tier.ValueKind != JsonValueKind.Object || !tier.TryGetProperty("tier", out var bound)
                            || bound.ValueKind != JsonValueKind.Object || Text(bound, "type") != "context"
                            || !bound.TryGetProperty("size", out var size) || !size.TryGetUInt64(out var above) || above == 0) continue;
                        var tierRates = ReadRates(tier, provider);
                        if (tierRates.Input is not null && tierRates.Output is not null && tierRates.IsValid)
                            tiers.Add(new ModelPriceTier { AboveTokens = above, Rates = tierRates });
                    }
                }
                // 旧目录没有显式阈值时才采用 legacy 的 200k，禁止覆盖新版的 272k 档位。
                if (tiers.Count == 0 && cost.TryGetProperty("context_over_200k", out var legacy) && legacy.ValueKind == JsonValueKind.Object)
                {
                    var tierRates = ReadRates(legacy, provider);
                    if (tierRates is { Input: not null, Output: not null, IsValid: true })
                        tiers.Add(new ModelPriceTier { AboveTokens = 200_000, Rates = tierRates });
                }
                var price = new ModelPrice { Rates = rates, Tiers = tiers };
                if (!price.IsValid) continue;
                entries.Add(new ModelPriceEntry
                {
                    Provider = provider,
                    Model = Text(model, "id") is { Length: > 0 } id ? id : item.Name,
                    CanonicalModel = Text(model, "canonical_model_id"),
                    Price = price,
                });
            }
        }
        if (entries.Count == 0) throw new InvalidDataException("价目表中没有有效的模型价格");
        return new PublicPriceCatalog { UpdatedAt = updatedAt, Entries = entries };
    }

    private static TokenPrices ReadRates(JsonElement value, string provider)
    {
        var input = Number(value, "input");
        var write = Number(value, "cache_write");
        return new TokenPrices
        {
            Input = input,
            Output = Number(value, "output"),
            CacheRead = Number(value, "cache_read"),
            CacheWrite = write,
            CacheWrite1h = Number(value, "cache_write_1h") ?? (provider == "anthropic" && write is > 0 ? input * 2 : null),
        };
    }

    private static decimal? Number(JsonElement value, string key) => value.TryGetProperty(key, out var n)
        && n.ValueKind == JsonValueKind.Number && n.TryGetDecimal(out var number) && number >= 0 ? number : null;

    private static string Text(JsonElement value, string key) => value.TryGetProperty(key, out var text)
        && text.ValueKind == JsonValueKind.String ? text.GetString() ?? string.Empty : string.Empty;
}
