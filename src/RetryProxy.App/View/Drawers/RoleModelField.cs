using RetryProxy.Core.Config;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace RetryProxy.View.Drawers;

/// <summary>Claude 单个角色的模型输入；未填写模型时，1M 自动跟随主模型。</summary>
public sealed class RoleModelField : StackPanel
{
    private readonly TextBlock _label = new();
    private readonly ComboBox _model = new() { IsEditable = true, IsTextSearchEnabled = false };
    private readonly CheckBox _context = new() { Margin = new Thickness(0, 6, 0, 12) };
    private bool _parentContext;
    private bool _explicitContext;
    private bool _updating;

    public RoleModelField()
    {
        _label.Margin = new Thickness(0, 0, 0, 4);
        Children.Add(_label);
        Children.Add(_model);
        Children.Add(_context);
        DrawerText.Bind(_context, ContentControl.ContentProperty, "1M 上下文");
        DrawerText.Bind(_model, ToolTipProperty, "留空跟随主模型");
        _model.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => UpdateContext()));
        _model.SelectionChanged += (_, _) => Dispatcher.InvokeAsync(UpdateContext);
        _context.Checked += (_, _) => RememberExplicitContext();
        _context.Unchecked += (_, _) => RememberExplicitContext();
    }

    public string RoleName
    {
        get => _label.Text;
        set
        {
            _label.Text = value;
            AutomationProperties.SetName(_model, value);
            AutomationProperties.SetName(_context, value + " 1M");
        }
    }

    public string ModelText => _model.Text;
    public bool ExplicitContext => _explicitContext;
    public void Load(RoleModel model, bool parentContext)
    {
        _parentContext = parentContext;
        _explicitContext = model.Context1M;
        _model.Text = model.Model;
        UpdateContext();
    }

    public void SetModels(IEnumerable<string> models)
    {
        var text = _model.Text;
        _model.ItemsSource = models;
        _model.Text = text;
    }

    public void SetParentContext(bool value)
    {
        _parentContext = value;
        UpdateContext();
    }

    public RoleModel Read() => new() { Model = _model.Text.Trim(), Context1M = _explicitContext };

    private void RememberExplicitContext()
    {
        if (!_updating && _context.IsEnabled) _explicitContext = _context.IsChecked == true;
    }

    private void UpdateContext()
    {
        _updating = true;
        _context.IsEnabled = !string.IsNullOrWhiteSpace(_model.Text);
        _context.IsChecked = _context.IsEnabled ? _explicitContext : _parentContext;
        _updating = false;
    }
}
