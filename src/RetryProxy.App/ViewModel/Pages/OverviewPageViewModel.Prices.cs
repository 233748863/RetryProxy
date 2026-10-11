using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetryProxy.Core.Metrics;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace RetryProxy.ViewModel.Pages;

public partial class StatisticsPriceTier : ObservableObject
{
    [ObservableProperty] private string _aboveTokens = string.Empty;
    [ObservableProperty] private string _input = string.Empty;
    [ObservableProperty] private string _output = string.Empty;
    [ObservableProperty] private string _cacheRead = string.Empty;
    [ObservableProperty] private string _cacheWrite = string.Empty;
    [ObservableProperty] private string _cacheWrite1h = string.Empty;

    public StatisticsPriceTier(TokenPrices? prices = null, ulong? above = null)
    {
        prices ??= new TokenPrices();
        AboveTokens = above?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        Input = Format(prices.Input); Output = Format(prices.Output); CacheRead = Format(prices.CacheRead);
        CacheWrite = Format(prices.CacheWrite); CacheWrite1h = Format(prices.CacheWrite1h);
    }

    private static string Format(decimal? value) => value?.ToString("0.############################", CultureInfo.InvariantCulture) ?? string.Empty;

    public TokenPrices Parse()
    {
        var result = new TokenPrices { Input = Number(Input), Output = Number(Output), CacheRead = Number(CacheRead), CacheWrite = Number(CacheWrite), CacheWrite1h = Number(CacheWrite1h) };
        if (result.Input is null || result.Output is null) throw new FormatException(StatisticsText.T("请填写普通输入和输出的单价；免费请填写 0"));
        return result;
    }

    private static decimal? Number(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!decimal.TryParse(value.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) || amount < 0)
            throw new FormatException(StatisticsText.T("单价须为不小于 0 的数字，小数点使用英文句点"));
        return amount;
    }
}

public partial class OverviewPageViewModel
{
    private static readonly HttpClient PriceHttp = new() { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 16 * 1024 * 1024 };
    private bool _loadingPrice;
    [ObservableProperty] private bool _isPriceEditorOpen;
    [ObservableProperty] private bool _isUpdatingPrices;
    [ObservableProperty] private string _editingModel = string.Empty;
    [ObservableProperty] private string _editingProvider = string.Empty;
    [ObservableProperty] private string _editingSource = string.Empty;
    [ObservableProperty] private string _priceFeedback = string.Empty;
    [ObservableProperty] private string _priceCatalogCaption = string.Empty;
    [ObservableProperty] private StatisticsPriceTier _basePrices = new();
    [ObservableProperty] private ObservableCollection<StatisticsPriceTier> _priceTiers = [];
    [ObservableProperty] private IReadOnlyList<string> _priceModels = [];
    [ObservableProperty] private IReadOnlyList<StatisticsChoice> _priceProviders = [];

    partial void OnEditingModelChanged(string value) { if (!_loadingPrice) LoadEditingPrice(); }
    partial void OnEditingProviderChanged(string value) { if (!_loadingPrice) LoadEditingPrice(); }

    private void UpdatePriceChoices()
    {
        _loadingPrice = true;
        var selectedModel = EditingModel ?? string.Empty;
        var selectedProvider = EditingProvider ?? string.Empty;
        var models = _prices.Models.Concat(ModelChoices.Where(o => o.Id.Length > 0 && o.Id != UsageRecord.Unknown).Select(o => o.Id))
            .Concat((_configService.Get().OtherConfig.StatisticsModelPrices ?? []).Select(o => o.Model))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!PriceModels.SequenceEqual(models)) PriceModels = models;
        var providers = new[] { new StatisticsChoice(string.Empty, StatisticsText.T("全部供应商（通用单价）")) }
            .Concat(_workspaceService.Workspace.Config.Providers.Select(p => new StatisticsChoice(p.Id, p.Name)))
            .Concat(ProviderChoices.Where(p => p.Id.Length > 0 && p.Id != UsageRecord.Unknown))
            .DistinctBy(p => p.Id).ToArray();
        if (!PriceProviders.SequenceEqual(providers)) PriceProviders = providers;
        EditingModel = selectedModel;
        EditingProvider = selectedProvider;
        PriceCatalogCaption = StatisticsText.T("公开价 · models.dev · {0} · {1} 个模型", _prices.Catalog.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd"), _prices.Catalog.Entries.Count);
        _loadingPrice = false;
    }

    [RelayCommand]
    private void OnOpenPrices()
    {
        IsPriceEditorOpen = !IsPriceEditorOpen;
        if (!IsPriceEditorOpen) return;
        if (string.IsNullOrEmpty(EditingModel))
        {
            EditingModel = !string.IsNullOrEmpty(SelectedModel) && SelectedModel != UsageRecord.Unknown ? SelectedModel
                : _report?.Records.FirstOrDefault(r => r.Model != UsageRecord.Unknown)?.Model ?? PriceModels.FirstOrDefault() ?? string.Empty;
            LoadEditingPrice();
        }
        UpdatePriceChoices();
    }

    private void LoadEditingPrice()
    {
        var (price, _) = _prices.Find(EditingProvider ?? string.Empty, EditingModel ?? string.Empty);
        BasePrices = new StatisticsPriceTier(price?.Rates);
        PriceTiers = new ObservableCollection<StatisticsPriceTier>((price?.Tiers ?? []).Select(t => new StatisticsPriceTier(t.Rates, t.AboveTokens)));
        UpdateEditingSource();
        PriceFeedback = string.Empty;
    }

    private void UpdateEditingSource()
    {
        var (_, source) = _prices.Find(EditingProvider ?? string.Empty, EditingModel ?? string.Empty);
        EditingSource = source switch
        {
            "provider_override" => StatisticsText.T("当前为该供应商的自定义单价"),
            "model_override" => StatisticsText.T("当前为全部供应商的自定义单价"),
            "" => StatisticsText.T("暂无公开价，填写后可计价"),
            _ => StatisticsText.T("当前使用公开价：{0}", source),
        };
    }

    [RelayCommand] private void OnAddPriceTier() => PriceTiers.Add(new StatisticsPriceTier());
    [RelayCommand] private void OnRemovePriceTier(StatisticsPriceTier tier) => PriceTiers.Remove(tier);

    [RelayCommand]
    private async Task OnSavePrice()
    {
        try
        {
            var model = (EditingModel ?? string.Empty).Trim();
            if (model.Length == 0 || model.Length > 256 || model.Any(char.IsControl))
                throw new FormatException(StatisticsText.T("请输入有效的模型名称（1 至 256 个字符）"));
            var tiers = PriceTiers.Select(tier =>
            {
                if (!ulong.TryParse(tier.AboveTokens, NumberStyles.None, CultureInfo.InvariantCulture, out var bound) || bound == 0)
                    throw new FormatException(StatisticsText.T("长上下文阈值须为大于 0 的整数"));
                return new ModelPriceTier { AboveTokens = bound, Rates = tier.Parse() };
            }).OrderBy(t => t.AboveTokens).ToList();
            var price = new ModelPrice { Rates = BasePrices.Parse(), Tiers = tiers };
            if (!price.IsValid) throw new FormatException(StatisticsText.T("长上下文阈值不能重复，请检查单价"));
            var provider = EditingProvider ?? string.Empty;
            var config = _configService.Get().OtherConfig;
            var previous = config.StatisticsModelPrices;
            var next = (previous ?? []).Where(p => !SamePrice(p, provider, model)).ToList();
            next.Add(new ModelPriceOverride { ProviderId = provider, Model = model, Price = price });
            config.StatisticsModelPrices = next;
            try { _configService.SaveChecked(); }
            catch { config.StatisticsModelPrices = previous ?? new(); throw; }
            _prices = CurrentPrices();
            LoadEditingPrice();
            PriceFeedback = StatisticsText.T("单价已保存，历史费用按当前单价重新估算");
            UpdatePriceChoices();
            if (_visible) await ReloadAsync();
        }
        catch (Exception error) { PriceFeedback = StatisticsText.T("单价未保存：{0}", error.Message); }
    }

    [RelayCommand]
    private async Task OnRestorePrice()
    {
        var config = _configService.Get().OtherConfig;
        var previous = config.StatisticsModelPrices;
        try
        {
            config.StatisticsModelPrices = (previous ?? []).Where(p => !SamePrice(p, EditingProvider ?? string.Empty, EditingModel ?? string.Empty)).ToList();
            try { _configService.SaveChecked(); }
            catch { config.StatisticsModelPrices = previous ?? new(); throw; }
            _prices = CurrentPrices();
            LoadEditingPrice();
            PriceFeedback = StatisticsText.T("已恢复继承单价");
            if (_visible) await ReloadAsync();
        }
        catch (Exception error) { PriceFeedback = StatisticsText.T("恢复失败：{0}", error.Message); }
    }

    private static bool SamePrice(ModelPriceOverride entry, string provider, string model) =>
        string.Equals(entry.ProviderId, provider, StringComparison.Ordinal) && string.Equals(entry.Model, model.Trim(), StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private async Task OnUpdatePublicPrices()
    {
        IsUpdatingPrices = true;
        PriceFeedback = StatisticsText.T("正在更新公开价…");
        try
        {
            var json = await PriceHttp.GetStringAsync(ModelPricing.SourceUrl);
            var catalog = await Task.Run(() => ModelPricing.ParseModelsDev(json, DateTimeOffset.UtcNow));
            var config = _configService.Get().OtherConfig;
            var previous = config.StatisticsPublicPrices;
            config.StatisticsPublicPrices = catalog;
            try { _configService.SaveChecked(); }
            catch { config.StatisticsPublicPrices = previous; throw; }
            _prices = CurrentPrices();
            UpdatePriceChoices();
            // 正在编辑的字段保持原样；更新目录不覆盖未保存的单价输入。
            PriceFeedback = StatisticsText.T("公开价已更新，共 {0} 个模型；自定义单价继续优先", catalog.Entries.Count);
            if (_visible) await ReloadAsync();
        }
        catch (Exception error) { PriceFeedback = StatisticsText.T("公开价更新失败，保留原价格：{0}", error.Message); }
        finally { IsUpdatingPrices = false; }
    }
}
