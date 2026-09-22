using RetryProxy.Core.Config;
using RetryProxy.Core.Workspace;
using RetryProxy.Service.I18n;
using RetryProxy.ViewModel;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace RetryProxy.View.Dialogs;

/// <summary>“一键准备”选项窗：本通道沿用默认配置，或选定另一个供应商开新通道单独准备。</summary>
public partial class PrepareOptionsDialog : ContentDialog
{
    private const string NewProviderKey = "\u0000new-provider";

    private readonly PreparationDialogState _state;
    private readonly ProxyWorkspace _workspace;
    private readonly ProxyRoute _route;
    private bool _initialized;

    public PrepareOptionsDialog(ContentPresenter? host, ProxyWorkspace workspace, PreparationDialogState state)
        : base(host)
    {
        _workspace = workspace;
        _state = state;
        _route = workspace.Config.Routes.First(route => route.Id == state.RouteId);
        InitializeComponent();
        var client = _route.ClientType.Label();
        var withKey = workspace.RouteKeepAlives.TryGetValue(_route.Id, out var watchdog) && watchdog.Snapshot().WithKey;
        SubtitleText.Text = $"通道“{_route.Name}” · {client} · 后台随机抽一道 Java 题问答，收到完整回复即准备完成。此处的选择同时决定承接通道之后自动保活使用的配置。";
        var switching = withKey ? "本通道当前用的是指定 Key，选此项会换回默认配置并丢弃该 Key 的会话。" : string.Empty;
        DefaultHint.Text = $"直接启动本机 {client}，供应商、模型和密钥均由 CLI 当前配置决定（例如 CC Switch 切换到的供应商）；准备成功后自动保活也沿用这套配置。{switching}";
        SeparateHint.Text = $"选定或新建服务商，程序为它开一条 {client} 通道并启动，后台 {client} 只把输入的 Key 经这条新通道转发到该服务商。本通道“{_route.Name}”、本机 CLI 配置和正在使用的供应商都不受影响；Key 只驻留内存，不写入配置或日志。新通道准备成功后，它的自动保活继续用这把 Key。";

        var items = new List<PickerItem>(workspace.Config.Providers.Select(provider => new PickerItem(provider.Name, provider.Name)))
        {
            new(NewProviderKey, I18nService.Instance.Translate("新增服务商…")),
        };
        ProviderBox.ItemsSource = items;
        ProviderBox.SelectedItem = state.Provider is { } name ? items.First(item => item.Key == name) : items[^1];
        NewProviderNameBox.Text = state.NewProviderName;
        NewProviderUrlBox.Text = state.NewProviderUrl;
        ApiKeyBox.Password = state.ApiKey;
        DefaultRadio.IsChecked = state.Mode == PrepareMode.Default;
        SeparateRadio.IsChecked = state.Mode == PrepareMode.SeparateProvider;
        _initialized = true;
        UpdateMode();
        UpdateProvider();
    }

    private void UpdateMode()
    {
        var separate = SeparateRadio.IsChecked == true;
        _state.Mode = separate ? PrepareMode.SeparateProvider : PrepareMode.Default;
        SeparatePanel.IsEnabled = separate;
        PrimaryButtonText = I18nService.Instance.Translate(separate ? "开新通道准备" : "开始准备");
    }

    private void UpdateProvider()
    {
        var item = ProviderBox.SelectedItem as PickerItem;
        _state.Provider = item is null || item.Key == NewProviderKey ? null : item.Key;
        NewProviderPanel.Visibility = _state.Provider is null ? Visibility.Visible : Visibility.Collapsed;
        PlanText.Text = _workspace.PlanText(_state);
    }

    private void ClearError()
    {
        _state.Error = null;
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        UpdateMode();
        ClearError();
    }

    private void OnProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        UpdateProvider();
        ClearError();
    }

    private void OnFieldChanged(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            ClearError();
        }
    }

    protected override void OnButtonClick(ContentDialogButton button)
    {
        if (button == ContentDialogButton.Primary)
        {
            UpdateMode();
            UpdateProvider();
            _state.NewProviderName = NewProviderNameBox.Text;
            _state.NewProviderUrl = NewProviderUrlBox.Text;
            _state.ApiKey = ApiKeyBox.Password;
            if (!_workspace.SubmitPrepareDialog(_state))
            {
                ErrorText.Text = _state.Error ?? string.Empty;
                ErrorText.Visibility = Visibility.Visible;
                return;
            }
        }

        base.OnButtonClick(button);
    }
}
