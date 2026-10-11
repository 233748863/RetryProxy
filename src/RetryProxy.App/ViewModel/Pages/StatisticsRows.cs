using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Windows.Media;

namespace RetryProxy.ViewModel.Pages;

// 条目按稳定身份复用，只通知改变的字段，保留界面容器、焦点和选择状态。
public sealed partial class StatisticsTokenTile(string id, string label, string value, string detail, string toolTip) : ObservableObject
{
    public string Id { get; } = id;
    [ObservableProperty] private string _label = label;
    [ObservableProperty] private string _value = value;
    [ObservableProperty] private string _detail = detail;
    [ObservableProperty] private string _toolTip = toolTip;

    internal void Update(StatisticsTokenTile next)
    {
        Label = next.Label; Value = next.Value; Detail = next.Detail; ToolTip = next.ToolTip;
    }
}

public sealed partial class StatisticsRankRow(string id, string name, Brush brush, string value, string detail, string toolTip, double share) : ObservableObject
{
    public string Id { get; } = id;
    [ObservableProperty] private string _name = name;
    [ObservableProperty] private Brush _brush = brush;
    [ObservableProperty] private string _value = value;
    [ObservableProperty] private string _detail = detail;
    [ObservableProperty] private string _toolTip = toolTip;
    [ObservableProperty] private double _share = share;

    internal void Update(StatisticsRankRow next)
    {
        Name = next.Name; Brush = next.Brush; Value = next.Value; Detail = next.Detail;
        ToolTip = next.ToolTip; Share = next.Share;
    }
}

public sealed partial class StatisticsHeatDay(DateTime date, bool isEnabled, Brush brush, string toolTip, bool isSelected) : ObservableObject
{
    public DateTime Date { get; } = date;
    [ObservableProperty] private bool _isEnabled = isEnabled;
    [ObservableProperty] private Brush _brush = brush;
    [ObservableProperty] private string _toolTip = toolTip;
    [ObservableProperty] private bool _isSelected = isSelected;

    internal void Update(StatisticsHeatDay next)
    {
        IsEnabled = next.IsEnabled; Brush = next.Brush; ToolTip = next.ToolTip; IsSelected = next.IsSelected;
    }
}

public sealed partial class StatisticsRequestRow(string routeId, string requestId, string time, string model, string provider,
    string clientKey, string status, Brush statusBrush, string tokens, string cache, string cost, string duration, string toolTip) : ObservableObject
{
    public (string RouteId, string RequestId) Identity { get; } = (routeId, requestId);
    [ObservableProperty] private string _time = time;
    [ObservableProperty] private string _model = model;
    [ObservableProperty] private string _provider = provider;
    [ObservableProperty] private string _clientKey = clientKey;
    [ObservableProperty] private string _status = status;
    [ObservableProperty] private Brush _statusBrush = statusBrush;
    [ObservableProperty] private string _tokens = tokens;
    [ObservableProperty] private string _cache = cache;
    [ObservableProperty] private string _cost = cost;
    [ObservableProperty] private string _duration = duration;
    [ObservableProperty] private string _toolTip = toolTip;

    internal void Update(StatisticsRequestRow next)
    {
        Time = next.Time; Model = next.Model; Provider = next.Provider; ClientKey = next.ClientKey;
        Status = next.Status; StatusBrush = next.StatusBrush; Tokens = next.Tokens; Cache = next.Cache;
        Cost = next.Cost; Duration = next.Duration; ToolTip = next.ToolTip;
    }
}
