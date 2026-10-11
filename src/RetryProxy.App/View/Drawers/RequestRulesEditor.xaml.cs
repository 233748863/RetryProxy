using RetryProxy.Core.Config;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace RetryProxy.View.Drawers;

public partial class RequestRulesEditor : UserControl
{
    private ClientType _client;
    public RequestRulesEditor() => InitializeComponent();

    public void Load(ClientType client, IEnumerable<RequestRule> rules)
    {
        _client = client;
        Rows.Children.Clear();
        foreach (var rule in rules) Add(rule);
        RefreshOrder();
    }

    public object CaptureState() => Rows.Children.Cast<RequestRuleField>().Select(field => field.CaptureState()).ToArray();

    public string? Read(out List<RequestRule> rules)
    {
        rules = new();
        foreach (var field in Rows.Children.Cast<RequestRuleField>())
        {
            if (field.Read(out var rule) is { } error) return $"请求规则 {rules.Count + 1}：{error}";
            rules.Add(rule);
        }
        return null;
    }

    private void Add(RequestRule rule)
    {
        var field = new RequestRuleField(_client, rule);
        field.MoveRequested += Move;
        field.RemoveRequested += Remove;
        Rows.Children.Add(field);
    }

    private void AddClicked(object sender, RoutedEventArgs e)
    {
        if (Rows.Children.Count >= RequestRule.MaxRules) return;
        Add(new RequestRule { ValueJson = "\"\"" });
        RefreshOrder();
        ((RequestRuleField)Rows.Children[^1]).FocusRule();
    }

    private void Move(RequestRuleField field, int direction)
    {
        var index = Rows.Children.IndexOf(field);
        var target = index + direction;
        if (target < 0 || target >= Rows.Children.Count) return;
        Rows.Children.Remove(field);
        Rows.Children.Insert(target, field);
        RefreshOrder();
        field.FocusRule();
    }

    private void Remove(RequestRuleField field)
    {
        Rows.Children.Remove(field);
        RefreshOrder();
        AddButton.Focus();
    }

    private void RefreshOrder()
    {
        for (var i = 0; i < Rows.Children.Count; i++) ((RequestRuleField)Rows.Children[i]).SetPosition(i, Rows.Children.Count);
        AddButton.IsEnabled = Rows.Children.Count < RequestRule.MaxRules;
    }
}
