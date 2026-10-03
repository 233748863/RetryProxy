using RetryProxy.Core.Cli;
using RetryProxy.Core.Config;
using RetryProxy.Core.Workspace;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;
using Border = System.Windows.Controls.Border;
using StackPanel = System.Windows.Controls.StackPanel;

namespace RetryProxy.View.Drawers;

public partial class ProviderDrawer : DrawerPage
{
    private readonly bool _isNew;
    private readonly string _initialState;
    private CancellationTokenSource? _fetch;
    private string _modelsSourceUrl;
    private string? _fetchMessage;
    private int? _fetchedCount;
    private bool _editingKey;
    private readonly Dictionary<string, Wpf.Ui.Controls.Button> _keyEditButtons = new();
    private readonly Dictionary<string, Wpf.Ui.Controls.Button> _keyDeleteButtons = new();

    public ProviderDrawer(ProviderEndpoint draft, bool isNew)
    {
        Draft = draft.Clone();
        _isNew = isNew;
        _modelsSourceUrl = Draft.BaseUrl.Trim().TrimEnd('/');
        InitializeComponent();
        NameBox.Text = Draft.Name;
        UrlBox.Text = Draft.BaseUrl;
        WebsiteBox.Text = Draft.WebsiteUrl;
        NotesBox.Text = Draft.Notes;
        AuthBox.SelectedIndex = Draft.AuthMode == ClaudeAuthMode.Bearer ? 0 : 1;
        ModelBox.Text = Draft.Models.Model;
        ContextCheck.IsChecked = Draft.Models.Context1M;
        OpusField.Load(Draft.Models.Opus, Draft.Models.Context1M);
        SonnetField.Load(Draft.Models.Sonnet, Draft.Models.Context1M);
        HaikuField.Load(Draft.Models.Haiku, Draft.Models.Context1M);
        FableField.Load(Draft.Models.Fable, Draft.Models.Context1M);
        ContextCheck.Checked += (_, _) => UpdateRoleContext();
        ContextCheck.Unchecked += (_, _) => UpdateRoleContext();
        ContextWindowBox.Text = Draft.Models.ContextWindow?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        AutoCompactBox.Text = Draft.Models.AutoCompactTokenLimit?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        var claude = Draft.ClientType == ClientType.Claude;
        AuthPanel.Visibility = ContextCheck.Visibility = RolesExpander.Visibility = claude ? Visibility.Visible : Visibility.Collapsed;
        CodexPanel.Visibility = claude ? Visibility.Collapsed : Visibility.Visible;
        UpdateModels();
        RefreshKeys();
        _initialState = CaptureState();
    }

    public ProviderEndpoint Draft { get; }
    public Func<ProviderKey?, Task<ProviderKey?>>? EditKeyAsync { get; set; }
    public Func<string, string?>? CannotDeleteKey { get; set; }
    public Func<string, string, Task<bool>>? ConfirmDeleteAsync { get; set; }
    public override string TitleKey => _isNew ? "新增供应商" : "编辑供应商";
    public override bool HasChanges => CaptureState() != _initialState;

    private string CaptureState() => DrawerText.Snapshot(new
    {
        NameBox.Text, Url = UrlBox.Text, Website = WebsiteBox.Text, Notes = NotesBox.Text,
        Auth = AuthBox.SelectedIndex, Model = ModelBox.Text, Context = ContextCheck.IsChecked,
        Opus = new { OpusField.ModelText, OpusField.ExplicitContext },
        Sonnet = new { SonnetField.ModelText, SonnetField.ExplicitContext },
        Haiku = new { HaikuField.ModelText, HaikuField.ExplicitContext },
        Fable = new { FableField.ModelText, FableField.ExplicitContext },
        Window = ContextWindowBox.Text, Compact = AutoCompactBox.Text, Draft.Keys, Draft.FetchedModels,
    });

    public string? ReadDraft()
    {
        long? window = null, compact = null;
        if (Draft.ClientType == ClientType.Codex)
        {
            if (!ParseOptionalPositive(ContextWindowBox.Text, out window)) return "上下文窗口必须留空或填写正整数";
            if (!ParseOptionalPositive(AutoCompactBox.Text, out compact)) return "自动压缩阈值必须留空或填写正整数";
        }
        var website = WebsiteBox.Text.Trim();
        if (website.Length > 0 && (!Uri.TryCreate(website, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https") || uri.Host.Length == 0 || uri.UserInfo.Length > 0))
            return "官网必须是有效的网址，且不能包含用户名或密码";
        if (_isNew && Draft.Keys.Count == 0) return "每个供应商至少需要一个 Key";
        foreach (var key in Draft.Keys)
            if (DrawerText.ValidateKey(key, Draft, false) is { } error) return error;

        Draft.Name = NameBox.Text.Trim();
        Draft.BaseUrl = UrlBox.Text.Trim().TrimEnd('/');
        Draft.WebsiteUrl = website;
        Draft.Notes = NotesBox.Text.Trim();
        Draft.AuthMode = Draft.ClientType == ClientType.Claude && AuthBox.SelectedIndex == 1 ? ClaudeAuthMode.ApiKey : ClaudeAuthMode.Bearer;
        Draft.Models.Model = ModelBox.Text.Trim();
        Draft.Models.Context1M = ContextCheck.IsChecked == true;
        Draft.Models.Opus = OpusField.Read();
        Draft.Models.Sonnet = SonnetField.Read();
        Draft.Models.Haiku = HaikuField.Read();
        Draft.Models.Fable = FableField.Read();
        Draft.Models.ContextWindow = window;
        Draft.Models.AutoCompactTokenLimit = compact;
        if (Draft.BaseUrl != _modelsSourceUrl) Draft.FetchedModels.Clear();
        try { Draft.Validate(); }
        catch (ConfigException error) { return error.Message; }
        return null;
    }

    private static bool ParseOptionalPositive(string text, out long? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0) return false;
        result = value;
        return true;
    }

    private void UpdateRoleContext()
    {
        foreach (var field in new[] { OpusField, SonnetField, HaikuField, FableField })
            field.SetParentContext(ContextCheck.IsChecked == true);
    }

    private void UpdateModels()
    {
        var text = ModelBox.Text;
        ModelBox.ItemsSource = Draft.FetchedModels.ToArray();
        ModelBox.Text = text;
        foreach (var field in new[] { OpusField, SonnetField, HaikuField, FableField }) field.SetModels(Draft.FetchedModels);
    }

    public void RefreshKeys()
    {
        var restoreKey = _keyEditButtons.FirstOrDefault(pair => pair.Value.IsKeyboardFocusWithin).Key;
        _keyEditButtons.Clear();
        _keyDeleteButtons.Clear();
        KeyRows.Children.Clear();
        foreach (var key in Draft.Keys)
        {
            var border = new Border { Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 6), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) };
            border.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = key.Name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = DrawerText.MaskKey(key.ApiKey), Margin = new Thickness(0, 4, 0, 0) });
            panel.Children.Add(new TextBlock
            {
                Text = key.ModelOverride is { Model.Length: > 0 } model ? model.Model : DrawerText.T("沿用供应商模型"),
                Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap,
            });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var edit = new Wpf.Ui.Controls.Button();
            DrawerText.Bind(edit, ContentControl.ContentProperty, "编辑");
            edit.Click += async (_, _) => await OpenKeyAsync(key);
            _keyEditButtons[key.Id] = edit;
            var delete = new Wpf.Ui.Controls.Button { Margin = new Thickness(8, 0, 0, 0), Appearance = ControlAppearance.Danger };
            DrawerText.Bind(delete, ContentControl.ContentProperty, "删除");
            var reason = DeleteRestriction(key.Id);
            delete.IsEnabled = reason is null;
            if (reason is not null)
            {
                DrawerText.Bind(delete, ToolTipProperty, reason);
                ToolTipService.SetShowOnDisabled(delete, true);
            }
            _keyDeleteButtons[key.Id] = delete;
            delete.Click += async (_, _) => await DeleteKeyAsync(key);
            actions.Children.Add(edit);
            actions.Children.Add(delete);
            panel.Children.Add(actions);
            border.Child = panel;
            KeyRows.Children.Add(border);
        }
        if (restoreKey is not null) RestoreKeyFocus(restoreKey);
    }

    /// <summary>托盘切换时只刷新删除限制，不重建输入框或更改草稿。</summary>
    public void RefreshDeleteRestrictions()
    {
        foreach (var (keyId, button) in _keyDeleteButtons)
        {
            var reason = DeleteRestriction(keyId);
            button.IsEnabled = reason is null;
            button.ClearValue(ToolTipProperty);
            if (reason is not null) DrawerText.Bind(button, ToolTipProperty, reason);
            ToolTipService.SetShowOnDisabled(button, true);
        }
    }

    private void RestoreKeyFocus(string keyId)
    {
        if (_keyEditButtons.TryGetValue(keyId, out var button))
            Dispatcher.InvokeAsync(() => { if (button.IsVisible) button.Focus(); });
    }

    private string? DeleteRestriction(string keyId) => CannotDeleteKey?.Invoke(keyId)
        ?? (Draft.Keys.Count <= 1 ? "每个供应商至少保留一个 Key；请改为删除供应商" : null);

    private async void AddKeyClicked(object sender, RoutedEventArgs e) => await OpenKeyAsync(null);

    private async Task OpenKeyAsync(ProviderKey? key)
    {
        if (_editingKey || EditKeyAsync is null) return;
        _editingKey = true;
        try
        {
            // 父表单尚未通过校验时也允许编辑 Key；传给子抽屉的默认模型取当前输入。
            Draft.Models.Model = ModelBox.Text.Trim();
            Draft.Models.Context1M = ContextCheck.IsChecked == true;
            var edited = await EditKeyAsync(key);
            if (edited is null || Lifetime.IsCancellationRequested) return;
            var index = key is null ? -1 : Draft.Keys.FindIndex(item => item.Id == key.Id);
            if (index >= 0) Draft.Keys[index] = edited.Clone();
            else Draft.Keys.Add(edited.Clone());
            RefreshKeys();
            RestoreKeyFocus(edited.Id);
        }
        catch (Exception) { SetError("无法打开 Key 编辑，请稍后重试"); }
        finally { _editingKey = false; }
    }

    private async Task DeleteKeyAsync(ProviderKey key)
    {
        if (_editingKey || ConfirmDeleteAsync is null) return;
        if (DeleteRestriction(key.Id) is { } reason) { SetError(reason); return; }
        _editingKey = true;
        try
        {
            if (!await ConfirmDeleteAsync("删除 Key", DrawerText.Format("确定删除 Key“{0}”？保存供应商后生效。", key.Name))) return;
            if (Lifetime.IsCancellationRequested) return;
            if (DeleteRestriction(key.Id) is { } latest) { SetError(latest); return; }
            Draft.Keys.RemoveAll(item => item.Id == key.Id);
            RefreshKeys();
            if (Draft.Keys.FirstOrDefault() is { } remaining) RestoreKeyFocus(remaining.Id);
        }
        catch (Exception) { SetError("无法显示确认，请稍后重试"); }
        finally { _editingKey = false; }
    }

    private async void FetchClicked(object sender, RoutedEventArgs e)
    {
        if (_fetch is not null) return;
        SetError(null);
        var apiKey = Draft.Keys.FirstOrDefault()?.ApiKey.Trim() ?? string.Empty;
        if (apiKey.Length == 0) { SetError("请先填写第一个 Key 的密钥"); return; }
        if (apiKey.Any(char.IsControl)) { SetError("API Key 不能包含控制字符"); return; }
        var provider = new ProviderEndpoint("模型查询", UrlBox.Text);
        try { provider.Validate(); }
        catch (ConfigException error) { SetError(error.Message); return; }
        var auth = AuthBox.SelectedIndex == 1 ? ClaudeAuthMode.ApiKey : ClaudeAuthMode.Bearer;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Lifetime);
        _fetch = cancellation;
        FetchButton.IsEnabled = false;
        CancelFetchButton.Visibility = Visibility.Visible;
        _fetchedCount = null;
        _fetchMessage = "正在获取模型…";
        UpdateFetchStatus();
        try
        {
            var models = await ProviderModelFetcher.FetchAsync(provider, apiKey, Draft.ClientType, cancellation.Token, auth);
            cancellation.Token.ThrowIfCancellationRequested();
            if (UrlBox.Text.Trim().TrimEnd('/') != provider.BaseUrl || Draft.Keys.FirstOrDefault()?.ApiKey.Trim() != apiKey
                || AuthBox.SelectedIndex != (auth == ClaudeAuthMode.Bearer ? 0 : 1))
            {
                _fetchMessage = "获取期间地址、认证方式或第一个 Key 已变化，请重新获取模型";
                return;
            }
            Draft.FetchedModels = models.ToList();
            _modelsSourceUrl = provider.BaseUrl;
            _fetchedCount = models.Count;
            _fetchMessage = null;
            UpdateModels();
        }
        catch (OperationCanceledException) { _fetchMessage = "已取消获取模型"; }
        catch (WorkspaceException error) { _fetchMessage = error.Message; }
        catch (Exception) { _fetchMessage = "获取模型失败，请检查服务商地址及网络连接"; }
        finally
        {
            _fetch = null;
            FetchButton.IsEnabled = true;
            CancelFetchButton.Visibility = Visibility.Collapsed;
            UpdateFetchStatus();
        }
    }

    private void CancelFetchClicked(object sender, RoutedEventArgs e) => _fetch?.Cancel();
    private void UpdateFetchStatus() => FetchStatus.Text = _fetchedCount is { } count
        ? DrawerText.Format("已获取 {0} 个模型，保存供应商后生效", count)
        : _fetchMessage is null ? string.Empty : DrawerText.Error(_fetchMessage);

    protected override void OnLanguageChanged()
    {
        RefreshKeys();
        UpdateFetchStatus();
    }
}
