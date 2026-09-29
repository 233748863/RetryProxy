using RetryProxy.Core.Config;
using RetryProxy.Service.I18n;
using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;

namespace RetryProxy.View.Drawers;

internal static class DrawerText
{
    public static string T(string key) => I18nService.Instance.Translate(key);
    public static string Format(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);
    public static string Snapshot(object value) => JsonSerializer.Serialize(value);
    public static string MaskKey(string value) => value.Length <= 8 ? "····" : $"{value[..4]}····{value[^4..]}";

    /// <summary>动态 Core 校验消息先拆出参数，再翻译模板，避免切换语言后仍显示带变量的中文原文。</summary>
    public static string Error(string message)
    {
        var direct = T(message);
        if (direct != message) return direct;
        var route = Regex.Match(message, "^(?:转发)?通道“([^”]*)”：(.+)$");
        if (route.Success) return Format("{0}：{1}", route.Groups[1].Value, Error(route.Groups[2].Value));
        var http = Regex.Match(message, "^获取模型失败：服务商返回 HTTP ([0-9]+)，请检查地址及 API Key$");
        if (http.Success) return Format("获取模型失败：服务商返回 HTTP {0}，请检查地址及 API Key", http.Groups[1].Value);
        foreach (var prefix in new[] { "服务商名称重复：", "本地端口重复：", "Key 名称重复：" })
            if (message.StartsWith(prefix, StringComparison.Ordinal)) return Format(prefix + "{0}", message[prefix.Length..]);
        // Core 的供应商校验使用中文引号包裹名称，把每个名称作为模板参数保留。
        var values = Regex.Matches(message, "“([^”]*)”").Select(match => (object)match.Groups[1].Value).ToArray();
        if (values.Length > 0)
        {
            var index = 0;
            var template = Regex.Replace(message, "“[^”]*”", _ => "“{" + index++ + "}”");
            return Format(template, values);
        }
        return direct;
    }

    public static void Bind(DependencyObject target, DependencyProperty property, string key)
    {
        BindingOperations.SetBinding(target, property, new Binding(nameof(I18nService.Revision))
        {
            Source = I18nService.Instance, Converter = new TranslationConverter(), ConverterParameter = key,
        });
    }

    private sealed class TranslationConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => T((string)parameter);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    public static string? ValidateKey(ProviderKey key, ProviderEndpoint provider, bool isNew)
    {
        if (string.IsNullOrWhiteSpace(key.Name)) return "Key 名称不能为空";
        if (string.IsNullOrWhiteSpace(key.ApiKey)) return "请填写 API Key";
        if (key.ApiKey.Any(char.IsControl)) return "API Key 不能包含控制字符";
        if (provider.Keys.Any(existing => (isNew || existing.Id != key.Id)
            && string.Equals(existing.Name.Trim(), key.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            return "同一供应商内的 Key 名称不能重复";
        return null;
    }
}
