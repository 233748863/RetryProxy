using RetryProxy.Core.Cli;
using RetryProxy.Core.Config;
using RetryProxy.Core.Workspace;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace RetryProxy.View.Drawers;

/// <summary>准备任务只在隔离草稿中编辑；保存与启动由宿主的 CommitAsync 统一提交。</summary>
public partial class PreparationDrawer : DrawerPage
{
    private sealed class KeyRow(CheckBox check, TextBlock name, TextBlock secret, TextBlock status)
    {
        public CheckBox Check { get; } = check;
        public TextBlock Name { get; } = name;
        public TextBlock Secret { get; } = secret;
        public TextBlock Status { get; } = status;
    }

    private readonly PreparationWorkspace _preparations;
    private readonly ProxyWorkspace _workspace;
    private readonly ClientType _client;
    private readonly bool _isNew;
    private readonly string _initialState;
    private readonly HashSet<string> _selectedKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ComboBoxItem> _providerItems = new(StringComparer.Ordinal);
    private readonly Dictionary<string, KeyRow> _keyRows = new(StringComparer.Ordinal);
    private string _providerId;
    private string? _choicesSnapshot;
    private string? _sourceSnapshot;
    private bool _initialized;
    private bool _updating;
    private bool _syncingSecret;
    private CancellationTokenSource? _fetch;
    private IReadOnlyList<string> _fetchedModels = Array.Empty<string>();
    private string? _fetchMessage;
    private int? _fetchedCount;

    public PreparationDrawer(PreparationWorkspace preparations, ProxyWorkspace workspace, PreparationDialogState draft, bool isNew)
    {
        _preparations = preparations;
        _workspace = workspace;
        _isNew = isNew;
        Draft = draft.Copy();
        _client = Draft.ClientType;
        _providerId = Draft.ProviderId ?? string.Empty;
        if (_providerId.Length == 0)
        {
            var currentId = workspace.Config.RouteFor(_client)?.CurrentProviderId;
            _providerId = workspace.Config.ProvidersFor(_client).FirstOrDefault(provider => provider.Id == currentId)?.Id
                ?? workspace.Config.ProvidersFor(_client).FirstOrDefault()?.Id ?? string.Empty;
        }
        var selected = Draft.KeyIds.Count > 0 ? Draft.KeyIds : string.IsNullOrEmpty(Draft.KeyId) ? [] : new List<string> { Draft.KeyId };
        _selectedKeys.UnionWith(isNew ? selected : selected.Take(1));
        _fetchedModels = draft.Models.ToArray();
        InitializeComponent();
        ModelBox.Text = Draft.SelectedModel ?? string.Empty;
        ModelBox.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(InputChanged));
        ProviderUrlBox.Text = Draft.ProviderUrl;
        SecretBox.Password = Draft.ApiKey;
        IdleMinutesBox.Text = Draft.IdleMinutes;
        CurrentRadio.IsChecked = Draft.Mode == PrepareMode.LocalProvider;
        ListRadio.IsChecked = Draft.Mode == PrepareMode.ListProvider;
        CustomRadio.IsChecked = Draft.Mode == PrepareMode.CustomProvider;
        foreach (var effort in ReasoningEffortExtensions.AvailableFor(_client))
        {
            var item = new ComboBoxItem { Tag = effort, Content = DrawerText.T(effort.Label()) };
            ReasoningEffortBox.Items.Add(item);
            if (effort == Draft.ReasoningEffort) ReasoningEffortBox.SelectedItem = item;
        }
        _initialized = true;
        RefreshChoices();
        UpdateText();
        _initialState = CaptureState();
    }

    public PreparationDialogState Draft { get; }
    public override string TitleKey => _isNew ? "新增准备" : "修改准备";
    public override string SaveButtonKey => _isNew ? "开始准备" : "保存并开始";
    public override bool HasChanges => CaptureState() != _initialState;

    private PrepareMode Mode => CustomRadio.IsChecked == true ? PrepareMode.CustomProvider
        : ListRadio.IsChecked == true ? PrepareMode.ListProvider : PrepareMode.LocalProvider;

    private string CaptureState() => DrawerText.Snapshot(new
    {
        Client = _client, Mode, Provider = _providerId, Keys = _selectedKeys.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
        Url = ProviderUrlBox.Text, Secret = SecretBox.Password, Model = ModelBox.Text,
        Effort = (ReasoningEffortBox.SelectedItem as ComboBoxItem)?.Tag, Idle = IdleMinutesBox.Text,
    });

    private ProviderEndpoint? ListProvider => _workspace.Config.ProvidersFor(_client).FirstOrDefault(provider => provider.Id == _providerId);

    private ProviderEndpoint? SourceProvider
    {
        get
        {
            var providerId = Mode == PrepareMode.ListProvider ? _providerId : _workspace.Config.RouteFor(_client)?.CurrentProviderId;
            return Mode == PrepareMode.CustomProvider ? null
                : _workspace.Config.ProvidersFor(_client).FirstOrDefault(provider => provider.Id == providerId);
        }
    }

    private List<string> OrderedKeys()
    {
        // 列表顺序决定“第一个选中 Key”，避免 HashSet 枚举顺序改变获取模型所用的凭据。
        var ordered = ListProvider?.Keys.Where(key => _selectedKeys.Contains(key.Id)).Select(key => key.Id).ToList() ?? [];
        ordered.AddRange(_selectedKeys.Where(id => !ordered.Contains(id)).OrderBy(id => id, StringComparer.Ordinal));
        return ordered;
    }

    private void CopyInputsToDraft()
    {
        Draft.ClientType = _client;
        Draft.Mode = Mode;
        Draft.ProviderId = Mode == PrepareMode.ListProvider ? _providerId : string.Empty;
        Draft.KeyIds = Mode == PrepareMode.ListProvider ? OrderedKeys() : [];
        Draft.KeyId = Draft.KeyIds.FirstOrDefault() ?? string.Empty;
        Draft.ProviderUrl = ProviderUrlBox.Text.Trim();
        Draft.ApiKey = SecretBox.Password.Trim();
        Draft.SelectedModel = ModelBox.Text.Trim();
        Draft.IdleMinutes = IdleMinutesBox.Text;
        if ((ReasoningEffortBox.SelectedItem as ComboBoxItem)?.Tag is ReasoningEffort effort) Draft.ReasoningEffort = effort;
        Draft.Error = null;
    }

    public string? ReadDraft()
    {
        if (!_isNew)
        {
            var task = _preparations.Find(Draft.TaskId);
            if (task is null) return "这项准备已删除，请关闭抽屉后重新添加";
            if (!task.CanStart) return "请先停止这项准备，再修改设置";
        }
        RefreshChoices();
        CopyInputsToDraft();
        var idle = UiText.ParseIdleMinutes(Draft.IdleMinutes);
        if (idle is null || idle < ConfigDefaults.MinKeepaliveIdleMinutes || idle > ConfigDefaults.MaxKeepaliveIdleMinutes)
            return "独立保活间隔请输入 0.5～1440 分钟";
        if ((ReasoningEffortBox.SelectedItem as ComboBoxItem)?.Tag is not ReasoningEffort effort || !effort.IsSupportedBy(_client))
            return "该客户端不支持所选思考强度，请重新选择";
        if (Mode == PrepareMode.CustomProvider && string.IsNullOrWhiteSpace(Draft.SelectedModel))
            return "请输入模型名称，或获取模型后选择一个用于准备";
        if (ValidateSelection() is { } selectionError) return selectionError;
        try { _preparations.ResolveCredential(FirstKeyDraft()); }
        catch (Exception error) when (error is WorkspaceException or CliException or ConfigException) { return error.Message; }
        return null;
    }

    private string? ValidateSelection()
    {
        if (Mode != PrepareMode.ListProvider) return null;
        var provider = ListProvider;
        if (provider is null) return "请选择有效的供应商";
        if (_selectedKeys.Count == 0) return "请至少选择一个 Key";
        if (!_isNew && _selectedKeys.Count != 1) return "修改准备任务时只能选择一个 Key";
        foreach (var id in _selectedKeys)
        {
            if (provider.Keys.All(key => key.Id != id)) return "选中的 Key 已删除，请重新选择";
            if (IsOccupied(id)) return "选中的 Key 已有准备任务，请修改已有任务或重新选择";
        }
        return null;
    }

    private PreparationDialogState FirstKeyDraft()
    {
        var first = Draft.Copy();
        if (first.Mode == PrepareMode.ListProvider) first.KeyIds = string.IsNullOrEmpty(first.KeyId) ? [] : [first.KeyId];
        return first;
    }

    private bool IsOccupied(string keyId) => _preparations.FindForKey(new PreparationKeyRef(_providerId, keyId)) is { } task
        && (_isNew || task.Id != Draft.TaskId);

    /// <summary>宿主可在每次刷新时调用；倒计时变化不触碰控件，配置或任务占用变化才更新选项。</summary>
    public void RefreshChoices()
    {
        if (!_initialized || Lifetime.IsCancellationRequested) return;
        Dispatcher.VerifyAccess();
        var providers = _workspace.Config.ProvidersFor(_client).ToArray();
        var current = _workspace.Config.RouteFor(_client);
        var snapshot = DrawerText.Snapshot(new
        {
            Providers = providers,
            CurrentProvider = current?.CurrentProviderId, CurrentKey = current?.CurrentKeyId,
            Occupied = providers.SelectMany(provider => provider.Keys.Select(key => new
            {
                Provider = provider.Id, Key = key.Id,
                Task = _preparations.FindForKey(new PreparationKeyRef(provider.Id, key.Id))?.Id,
            })).ToArray(),
        });
        if (_choicesSnapshot == snapshot) return;
        _choicesSnapshot = snapshot;
        RefreshProviders(providers);
        RefreshKeyRows();
        var source = CaptureSource();
        if (_sourceSnapshot is not null && _sourceSnapshot != source) ResetModelSource();
        else
        {
            _sourceSnapshot = source;
            UpdateModelChoices();
        }
        UpdateText();
    }

    private void RefreshProviders(IReadOnlyList<ProviderEndpoint> providers)
    {
        _updating = true;
        try
        {
            var ids = providers.Select(provider => provider.Id).ToList();
            if (_providerId.Length > 0 && !ids.Contains(_providerId)) ids.Add(_providerId);
            foreach (var id in _providerItems.Keys.Where(id => !ids.Contains(id)).ToArray())
            {
                ProviderBox.Items.Remove(_providerItems[id]);
                _providerItems.Remove(id);
            }
            for (var index = 0; index < ids.Count; index++)
            {
                var id = ids[index];
                if (!_providerItems.TryGetValue(id, out var item))
                    _providerItems[id] = item = new ComboBoxItem { Tag = id };
                item.Content = providers.FirstOrDefault(provider => provider.Id == id)?.Name ?? DrawerText.T("原供应商已删除，请重新选择");
                if (ProviderBox.Items.IndexOf(item) != index)
                {
                    ProviderBox.Items.Remove(item);
                    ProviderBox.Items.Insert(index, item);
                }
            }
            ProviderBox.SelectedItem = _providerItems.GetValueOrDefault(_providerId);
        }
        finally { _updating = false; }
    }

    private void RefreshKeyRows()
    {
        var provider = ListProvider;
        var ids = provider?.Keys.Select(key => key.Id).ToList() ?? [];
        ids.AddRange(_selectedKeys.Where(id => !ids.Contains(id)).OrderBy(id => id, StringComparer.Ordinal));
        var focusedKey = _keyRows.FirstOrDefault(pair => pair.Value.Check.IsKeyboardFocusWithin).Key;
        var focusedElement = Keyboard.FocusedElement;
        _updating = true;
        try
        {
            foreach (var id in _keyRows.Keys.Where(id => !ids.Contains(id)).ToArray())
            {
                KeyRows.Children.Remove(_keyRows[id].Check);
                _keyRows.Remove(id);
            }
            for (var index = 0; index < ids.Count; index++)
            {
                var id = ids[index];
                if (!_keyRows.TryGetValue(id, out var row)) _keyRows[id] = row = CreateKeyRow(id);
                var key = provider?.Keys.FirstOrDefault(key => key.Id == id);
                var occupied = key is not null && IsOccupied(id);
                row.Name.Text = key?.Name ?? DrawerText.T("原 Key 已删除，请重新选择");
                row.Secret.Text = key is null ? string.Empty : DrawerText.MaskKey(key.ApiKey);
                row.Secret.Visibility = key is null ? Visibility.Collapsed : Visibility.Visible;
                row.Status.Text = occupied ? DrawerText.T("已有任务") : string.Empty;
                row.Status.Visibility = occupied ? Visibility.Visible : Visibility.Collapsed;
                row.Check.IsChecked = _selectedKeys.Contains(id);
                row.Check.IsEnabled = key is not null && !occupied;
                AutomationProperties.SetName(row.Check, occupied
                    ? DrawerText.Format("{0} · 已有任务", row.Name.Text) : row.Name.Text);
                if (KeyRows.Children.IndexOf(row.Check) != index)
                {
                    KeyRows.Children.Remove(row.Check);
                    KeyRows.Children.Insert(index, row.Check);
                }
            }
            NoKeysText.Text = DrawerText.T(provider is null ? "请先选择供应商" : "该供应商还没有 Key");
            NoKeysText.Visibility = ids.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ClearSelectionButton.IsEnabled = _selectedKeys.Count > 0;
        }
        finally { _updating = false; }
        // 重用未变的 Key 行；仅在焦点所在行被删除、禁用或移动时恢复到可用控件。
        if (focusedKey is not null && !ReferenceEquals(Keyboard.FocusedElement, focusedElement))
        {
            if (_keyRows.TryGetValue(focusedKey, out var row) && row.Check.IsEnabled) row.Check.Focus();
            else ProviderBox.Focus();
        }
    }

    private KeyRow CreateKeyRow(string id)
    {
        var check = new CheckBox { Margin = new Thickness(0, 0, 0, 8), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(check, "PreparationKey_" + id);
        var name = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        var secret = new TextBlock { Margin = new Thickness(0, 3, 0, 0) };
        var status = new TextBlock { Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap };
        secret.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var content = new StackPanel();
        content.Children.Add(name);
        content.Children.Add(secret);
        content.Children.Add(status);
        check.Content = content;
        check.Checked += (_, _) => KeySelectionChanged(id, true);
        check.Unchecked += (_, _) => KeySelectionChanged(id, false);
        return new KeyRow(check, name, secret, status);
    }

    private void KeySelectionChanged(string id, bool selected)
    {
        if (_updating || !_initialized) return;
        if (selected && !IsOccupied(id))
        {
            if (!_isNew) _selectedKeys.Clear();
            _selectedKeys.Add(id);
        }
        else _selectedKeys.Remove(id);
        RefreshKeyRows();
        ResetModelSource();
        InputEdited();
    }

    private void ProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _updating || (ProviderBox.SelectedItem as ComboBoxItem)?.Tag is not string id || id == _providerId) return;
        _providerId = id;
        _selectedKeys.Clear();
        RefreshKeyRows();
        ResetModelSource();
        InputEdited();
    }

    private void SourceChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _updating) return;
        ResetModelSource();
        InputEdited();
    }

    private void ClearSelectionClicked(object sender, RoutedEventArgs e)
    {
        _selectedKeys.Clear();
        RefreshKeyRows();
        ResetModelSource();
        InputEdited();
    }

    private void InputChanged(object sender, TextChangedEventArgs e) => InputEdited();
    private void InputSelectionChanged(object sender, SelectionChangedEventArgs e) => InputEdited();
    private void InputEdited()
    {
        if (!_initialized || _updating) return;
        SetError(null);
        UpdateText();
    }

    private void CredentialChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initialized || _updating) return;
        if (Mode == PrepareMode.CustomProvider) ResetModelSource();
        InputEdited();
    }

    private void SecretChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingSecret || VisibleSecretBox is null) return;
        _syncingSecret = true;
        VisibleSecretBox.Text = ShowSecretCheck?.IsChecked == true ? SecretBox.Password : string.Empty;
        _syncingSecret = false;
        if (!_initialized) return;
        if (Mode == PrepareMode.CustomProvider) ResetModelSource();
        InputEdited();
    }

    private void VisibleSecretChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingSecret || SecretBox is null || ShowSecretCheck?.IsChecked != true) return;
        _syncingSecret = true;
        SecretBox.Password = VisibleSecretBox.Text;
        _syncingSecret = false;
        if (!_initialized) return;
        if (Mode == PrepareMode.CustomProvider) ResetModelSource();
        InputEdited();
    }

    private void SecretVisibilityChanged(object sender, RoutedEventArgs e)
    {
        if (SecretBox is null || VisibleSecretBox is null) return;
        var show = ShowSecretCheck.IsChecked == true;
        _syncingSecret = true;
        VisibleSecretBox.Text = show ? SecretBox.Password : string.Empty;
        SecretBox.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        VisibleSecretBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _syncingSecret = false;
    }

    private string CaptureSource()
    {
        var provider = SourceProvider;
        var keyId = Mode == PrepareMode.ListProvider ? OrderedKeys().FirstOrDefault() : _workspace.Config.RouteFor(_client)?.CurrentKeyId;
        return DrawerText.Snapshot(new
        {
            Mode, Provider = provider?.Id,
            Keys = Mode == PrepareMode.ListProvider ? OrderedKeys() : new List<string> { keyId ?? string.Empty },
            Url = Mode == PrepareMode.CustomProvider ? ProviderUrlBox.Text : provider?.BaseUrl,
            Secret = Mode == PrepareMode.CustomProvider ? SecretBox.Password : provider?.Keys.FirstOrDefault(key => key.Id == keyId)?.ApiKey,
            Auth = provider?.AuthMode,
        });
    }

    private void ResetModelSource()
    {
        CancelFetch();
        _fetchedModels = Array.Empty<string>();
        Draft.Models = Array.Empty<string>();
        _fetchedCount = null;
        _fetchMessage = null;
        _sourceSnapshot = CaptureSource();
        UpdateModelChoices();
        UpdateFetchStatus();
    }

    private void UpdateModelChoices()
    {
        var provider = SourceProvider;
        var models = _fetchedModels.Concat(provider?.FetchedModels ?? Enumerable.Empty<string>())
            .Concat(new[] { provider?.Models.Model ?? string.Empty })
            .Concat(provider?.Keys.Select(key => key.ModelOverride?.Model ?? string.Empty) ?? Enumerable.Empty<string>())
            .Where(model => !string.IsNullOrWhiteSpace(model)).Distinct(StringComparer.Ordinal).ToArray();
        var text = ModelBox.Text;
        var editor = ModelBox.Template?.FindName("PART_EditableTextBox", ModelBox) as TextBox;
        var start = editor?.SelectionStart ?? 0;
        var length = editor?.SelectionLength ?? 0;
        _updating = true;
        try
        {
            // 重新获取相同列表时也清除旧选中项、保留输入；否则清空文本后再次选择同一模型不会触发回填。
            ModelBox.SelectedIndex = -1;
            ModelBox.ItemsSource = models;
            ModelBox.Text = text;
            editor?.Select(Math.Min(start, text.Length), Math.Min(length, Math.Max(0, text.Length - start)));
        }
        finally { _updating = false; }
    }

    private async void FetchClicked(object sender, RoutedEventArgs e)
    {
        if (_fetch is not null || Lifetime.IsCancellationRequested) return;
        SetError(null);
        RefreshChoices();
        CopyInputsToDraft();
        if (ValidateSelection() is { } selectionError) { SetError(selectionError); return; }
        CliCredential credential;
        ProviderEndpoint provider;
        try
        {
            credential = _preparations.ResolveCredential(FirstKeyDraft());
            provider = new ProviderEndpoint("独立准备", credential.BaseUrl);
        }
        catch (Exception error) when (error is WorkspaceException or CliException or ConfigException)
        {
            SetError(error.Message);
            return;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Lifetime);
        var source = CaptureSource();
        bool ReferenceMatchesSource()
        {
            if (!ReferenceEquals(_fetch, cancellation) || cancellation.IsCancellationRequested || Lifetime.IsCancellationRequested) return false;
            if (source == CaptureSource()) return true;
            ResetModelSource();
            return false;
        }
        _fetch = cancellation;
        _fetchMessage = "正在获取模型…";
        _fetchedCount = null;
        FetchButton.IsEnabled = false;
        CancelFetchButton.Visibility = Visibility.Visible;
        UpdateFetchStatus();
        try
        {
            var models = await ProviderModelFetcher.FetchAsync(provider, credential.ApiKey, _client, cancellation.Token, credential.AuthMode);
            // 来源变化即废弃整次响应；不改模型输入，用户在等待期间的输入与光标必须保留。
            if (!ReferenceMatchesSource()) return;
            _fetchedModels = models.ToArray();
            Draft.Models = _fetchedModels;
            _fetchedCount = models.Count;
            _fetchMessage = null;
            UpdateModelChoices();
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_fetch, cancellation) && !Lifetime.IsCancellationRequested) _fetchMessage = "已取消获取模型";
        }
        catch (WorkspaceException error)
        {
            if (ReferenceMatchesSource()) _fetchMessage = error.Message;
        }
        catch (Exception)
        {
            if (ReferenceMatchesSource()) _fetchMessage = "获取模型失败，请检查服务商地址及网络连接";
        }
        finally
        {
            if (ReferenceEquals(_fetch, cancellation))
            {
                _fetch = null;
                if (!Lifetime.IsCancellationRequested)
                {
                    FetchButton.IsEnabled = true;
                    CancelFetchButton.Visibility = Visibility.Collapsed;
                    UpdateFetchStatus();
                }
            }
        }
    }

    private void CancelFetch()
    {
        var fetch = _fetch;
        _fetch = null;
        fetch?.Cancel();
        FetchButton.IsEnabled = true;
        CancelFetchButton.Visibility = Visibility.Collapsed;
    }

    private void CancelFetchClicked(object sender, RoutedEventArgs e)
    {
        CancelFetch();
        _fetchMessage = "已取消获取模型";
        _fetchedCount = null;
        UpdateFetchStatus();
    }

    private void UpdateFetchStatus() => FetchStatus.Text = _fetchedCount is { } count
        ? DrawerText.Format("已获取 {0} 个模型", count)
        : _fetchMessage is null ? string.Empty : DrawerText.Error(_fetchMessage);

    private void UpdateText()
    {
        ClientText.Text = DrawerText.T(_client.Label());
        ListPanel.Visibility = Mode == PrepareMode.ListProvider ? Visibility.Visible : Visibility.Collapsed;
        CustomPanel.Visibility = Mode == PrepareMode.CustomProvider ? Visibility.Visible : Visibility.Collapsed;
        CurrentTargetText.Visibility = Mode == PrepareMode.LocalProvider ? Visibility.Visible : Visibility.Collapsed;
        var current = _workspace.Config.RouteFor(_client);
        var currentProvider = _workspace.Config.ProvidersFor(_client).FirstOrDefault(provider => provider.Id == current?.CurrentProviderId);
        var currentKey = currentProvider?.Keys.FirstOrDefault(key => key.Id == current?.CurrentKeyId);
        CurrentTargetText.Text = currentProvider is not null && currentKey is not null
            ? DrawerText.Format("当前：{0} · {1}", currentProvider.Name, currentKey.Name)
            : DrawerText.T("尚未选择当前 Key，请先在供应商页选择");
        SelectionHint.Text = DrawerText.T(_isNew ? "可选择多个 Key，每个 Key 创建一项任务。" : "修改任务时只能选择一个 Key。");
        ModelHint.Text = DrawerText.T(Mode == PrepareMode.CustomProvider ? "手动填写时必须指定模型" : "留空沿用各 Key 的模型");
        PlanText.Text = DrawerText.Format("将开始 {0} 项准备，成功后按设定间隔独立保活。", Mode == PrepareMode.ListProvider ? _selectedKeys.Count : 1);
        SourcePlanText.Text = DrawerText.T(Mode switch
        {
            PrepareMode.LocalProvider => "每次开始时读取当前 Key 的最新配置；填写模型后使用指定模型，运行中的任务沿用开始时的配置。",
            PrepareMode.ListProvider => "每次开始时读取所选 Key 的最新配置；填写模型后，所有选中 Key 共用该模型。",
            _ => "使用手动填写的地址、密钥和模型进行准备。",
        });
    }

    protected override void OnLanguageChanged()
    {
        if (!_initialized || Lifetime.IsCancellationRequested) return;
        RefreshProviders(_workspace.Config.ProvidersFor(_client).ToArray());
        RefreshKeyRows();
        foreach (ComboBoxItem item in ReasoningEffortBox.Items)
            if (item.Tag is ReasoningEffort effort) item.Content = DrawerText.T(effort.Label());
        UpdateText();
        UpdateFetchStatus();
    }

    public override void CancelPendingOperations()
    {
        base.CancelPendingOperations();
        if (_initialized) CancelFetch();
    }

    public override void Dispose()
    {
        base.Dispose();
        _syncingSecret = true;
        SecretBox.Clear();
        VisibleSecretBox.Clear();
    }
}
