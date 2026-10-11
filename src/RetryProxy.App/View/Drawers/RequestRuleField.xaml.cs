using RetryProxy.Core.Config;
using System;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace RetryProxy.View.Drawers;

public partial class RequestRuleField : UserControl
{
    private bool _loading = true;
    public event Action<RequestRuleField, int>? MoveRequested;
    public event Action<RequestRuleField>? RemoveRequested;

    public RequestRuleField(ClientType client, RequestRule rule)
    {
        InitializeComponent();
        AddParameter("自定义参数", "");
        AddParameter("温度", "temperature");
        AddParameter("采样范围", "top_p");
        AddParameter("最大输出用量", client == ClientType.Claude ? "max_tokens" : "max_output_tokens");
        AddParameter("思考强度", client == ClientType.Claude ? "output_config.effort" : "reasoning.effort");
        if (client == ClientType.Claude)
        {
            AddParameter("思考模式", "thinking.type");
            AddParameter("思考用量上限", "thinking.budget_tokens");
        }
        else
        {
            AddParameter("服务等级", "service_tier");
            AddParameter("并行工具调用", "parallel_tool_calls");
        }
        EnabledCheck.IsChecked = rule.Enabled;
        ModelPatternBox.Text = rule.ModelPattern;
        PathBox.Text = rule.Path;
        ParameterBox.SelectedItem = ParameterBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == rule.Path)
            ?? ParameterBox.Items[0];
        ActionBox.SelectedIndex = (int)rule.Action;
        try
        {
            using var value = JsonDocument.Parse(rule.ValueJson);
            ValueTypeBox.SelectedIndex = value.RootElement.ValueKind switch
            {
                JsonValueKind.Number => 0, JsonValueKind.String => 1, JsonValueKind.True or JsonValueKind.False => 2, _ => 3,
            };
            ValueBox.Text = value.RootElement.ValueKind == JsonValueKind.String ? value.RootElement.GetString()! : rule.ValueJson;
            BooleanBox.SelectedIndex = value.RootElement.ValueKind == JsonValueKind.False ? 1 : 0;
        }
        catch (JsonException) { ValueTypeBox.SelectedIndex = 1; ValueBox.Text = rule.ValueJson; }
        _loading = false;
        UpdateVisibility();
    }

    private void AddParameter(string label, string path)
    {
        var item = new ComboBoxItem { Tag = path };
        DrawerText.Bind(item, ContentControl.ContentProperty, label);
        ParameterBox.Items.Add(item);
    }

    public object CaptureState() => new
    {
        Enabled = EnabledCheck.IsChecked, Model = ModelPatternBox.Text, Path = PathBox.Text,
        Action = ActionBox.SelectedIndex, Type = ValueTypeBox.SelectedIndex, Value = ValueBox.Text, Boolean = BooleanBox.SelectedIndex,
    };

    public string? Read(out RequestRule rule)
    {
        rule = new RequestRule
        {
            Enabled = EnabledCheck.IsChecked == true, ModelPattern = ModelPatternBox.Text.Trim(), Path = PathBox.Text.Trim(),
            Action = (RequestRuleAction)ActionBox.SelectedIndex,
        };
        if (rule.Action != RequestRuleAction.Remove)
        {
            rule.ValueJson = ValueTypeBox.SelectedIndex switch
            {
                1 => JsonSerializer.Serialize(ValueBox.Text), 2 => BooleanBox.SelectedIndex == 0 ? "true" : "false", _ => ValueBox.Text.Trim(),
            };
            if (ValueTypeBox.SelectedIndex == 0)
            {
                try
                {
                    using var value = JsonDocument.Parse(rule.ValueJson);
                    if (value.RootElement.ValueKind != JsonValueKind.Number) return "数值类型必须填写有效数字";
                }
                catch (JsonException) { return "数值类型必须填写有效数字"; }
            }
        }
        try { rule.Validate(); }
        catch (ConfigException error) { return error.Message; }
        return null;
    }

    public void SetPosition(int index, int count)
    {
        NumberText.Text = $"#{index + 1}";
        UpButton.IsEnabled = index > 0;
        DownButton.IsEnabled = index + 1 < count;
    }

    public void FocusRule() => EnabledCheck.Focus();

    private void ParameterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ParameterBox.SelectedItem is ComboBoxItem { Tag: string path } && path.Length > 0) PathBox.Text = path;
        UpdateVisibility();
    }
    private void ActionChanged(object sender, SelectionChangedEventArgs e) { if (!_loading) UpdateVisibility(); }
    private void ValueTypeChanged(object sender, SelectionChangedEventArgs e) { if (!_loading) UpdateVisibility(); }
    private void UpdateVisibility()
    {
        CustomPathPanel.Visibility = ParameterBox.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        ValuePanel.Visibility = ActionBox.SelectedIndex == 2 ? Visibility.Collapsed : Visibility.Visible;
        BooleanBox.Visibility = ValueTypeBox.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        ValueBox.Visibility = ValueTypeBox.SelectedIndex == 2 ? Visibility.Collapsed : Visibility.Visible;
    }
    private void UpClicked(object sender, RoutedEventArgs e) => MoveRequested?.Invoke(this, -1);
    private void DownClicked(object sender, RoutedEventArgs e) => MoveRequested?.Invoke(this, 1);
    private void RemoveClicked(object sender, RoutedEventArgs e) => RemoveRequested?.Invoke(this);
}
