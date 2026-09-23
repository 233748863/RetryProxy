using RetryProxy.Core.Config;
using RetryProxy.Core.Workspace;
using RetryProxy.Service.I18n;
using RetryProxy.ViewModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using RetryProxy.Core.Cli;
using Wpf.Ui.Controls;

namespace RetryProxy.View.Dialogs;

/// <summary>“一键准备”选项窗：默认配置、编辑本通道或新建独立通道。</summary>
public partial class PrepareOptionsDialog : ContentDialog
{
    private const string NewProviderKey = "\u0000new-provider";

    private readonly PreparationDialogState _state;
    private readonly ProxyWorkspace _workspace;
    private readonly ProxyRoute _route;
    private bool _initialized;
    private CancellationTokenSource? _modelFetch;

    public PrepareOptionsDialog(ContentPresenter? host, ProxyWorkspace workspace, PreparationDialogState state)
        : base(host)
    {
        _workspace = workspace;
        _state = state;
        _route = workspace.Config.Routes.First(route => route.Id == state.RouteId);
        InitializeComponent();
        ModelBox.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(OnModelTextChanged));
        ModelBox.Text = state.SelectedModel ?? string.Empty;
        var client = _route.ClientType.Label();
        var withKey = workspace.RouteKeepAlives.TryGetValue(_route.Id, out var watchdog) && watchdog.Snapshot().WithKey;
        SubtitleText.Text = $"通道“{_route.Name}” · {client} · 后台随机抽一道 Java 题问答，收到完整回复即准备完成。此处的选择同时决定承接通道之后自动保活使用的配置。";
        var switching = withKey ? "本通道当前用的是指定 Key，选此项会换回默认配置并丢弃该 Key 的会话。" : string.Empty;
        DefaultHint.Text = $"直接启动本机 {client}，供应商、模型和密钥均由 CLI 当前配置决定（例如 CC Switch 切换到的供应商）；准备成功后自动保活也沿用这套配置。{switching}";
        SeparateHint.Text = "本通道和新通道各自保存服务商地址、模型及加密的 API Key；修改当前通道时不会改变其他通道，也不会修改本机 CLI 配置。";

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
        CurrentRadio.IsChecked = state.Mode == PrepareMode.CurrentRoute;
        SeparateRadio.IsChecked = state.Mode == PrepareMode.SeparateProvider;
        _initialized = true;
        Unloaded += (_, _) => ResetModels();
        UpdateMode();
        UpdateProvider();
    }

    private void UpdateMode()
    {
        _state.Mode = CurrentRadio.IsChecked == true ? PrepareMode.CurrentRoute
            : SeparateRadio.IsChecked == true ? PrepareMode.SeparateProvider : PrepareMode.Default;
        SeparatePanel.IsEnabled = _state.Mode != PrepareMode.Default;
        PrimaryButtonText = I18nService.Instance.Translate(_state.Mode switch
        {
            PrepareMode.CurrentRoute => "更新本通道并准备",
            PrepareMode.SeparateProvider => "创建独立通道并准备",
            _ => "开始准备",
        });
    }

    private void UpdateProvider()
    {
        var item = ProviderBox.SelectedItem as PickerItem;
        var current = _state.Mode == PrepareMode.CurrentRoute;
        _state.Provider = current || item is null || item.Key == NewProviderKey ? null : item.Key;
        ProviderLabel.Visibility = current ? Visibility.Collapsed : Visibility.Visible;
        ProviderBox.Visibility = current ? Visibility.Collapsed : Visibility.Visible;
        NewProviderPanel.Visibility = current || _state.Provider is null ? Visibility.Visible : Visibility.Collapsed;
        PlanText.Text = _workspace.PlanText(_state);
    }

    private void ClearError()
    {
        _state.Error = null;
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void ResetModels(bool clearModel = false)
    {
        _modelFetch?.Cancel();
        _modelFetch = null;
        var model = clearModel ? string.Empty : ModelBox.Text;
        _state.ClearModels();
        ModelBox.ItemsSource = null;
        ModelBox.Text = model;
        _state.SelectedModel = model;
        FetchModelsButton.IsEnabled = true;
        ModelsLoadingText.Visibility = Visibility.Collapsed;
    }

    private void ShowError(string message)
    {
        _state.Error = message;
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private async void OnFetchModels(object sender, RoutedEventArgs e)
    {
        _state.NewProviderName = NewProviderNameBox.Text;
        _state.NewProviderUrl = NewProviderUrlBox.Text;
        _state.ApiKey = ApiKeyBox.Password;
        ResetModels();
        ClearError();

        ProviderEndpoint provider;
        string apiKey;
        try
        {
            provider = _workspace.SeparateTargetProvider(_state);
            apiKey = CliCredential.Create(_state.ApiKey, provider.BaseUrl).ApiKey;
        }
        catch (WorkspaceException error)
        {
            ShowError(error.Message);
            return;
        }
        catch (CliException error)
        {
            ShowError(error.Message);
            return;
        }

        var cancellation = new CancellationTokenSource();
        _modelFetch = cancellation;
        FetchModelsButton.IsEnabled = false;
        ModelsLoadingText.Visibility = Visibility.Visible;
        try
        {
            var models = await ProviderModelFetcher.FetchAsync(provider, apiKey, _route.ClientType, cancellation.Token);
            if (!ReferenceEquals(_modelFetch, cancellation))
            {
                return;
            }

            _state.Models = models;
            var model = ModelBox.Text;
            ModelBox.ItemsSource = models;
            ModelBox.Text = model;
        }
        catch (OperationCanceledException)
        {
        }
        catch (WorkspaceException error)
        {
            if (ReferenceEquals(_modelFetch, cancellation))
            {
                ShowError(error.Message);
            }
        }
        finally
        {
            if (ReferenceEquals(_modelFetch, cancellation))
            {
                _modelFetch = null;
                FetchModelsButton.IsEnabled = true;
                ModelsLoadingText.Visibility = Visibility.Collapsed;
            }

            cancellation.Dispose();
        }
    }

    private void OnModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized)
        {
            _state.SelectedModel = ModelBox.SelectedItem as string ?? ModelBox.Text;
            ClearError();
        }
    }

    private void OnModelTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initialized)
        {
            _state.SelectedModel = ModelBox.Text;
            ClearError();
        }
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        UpdateMode();
        UpdateProvider();
        ResetModels(clearModel: true);
        ClearError();
    }

    private void OnProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        UpdateProvider();
        ResetModels(clearModel: true);
        ClearError();
    }

    private void OnFieldChanged(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            ResetModels();
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
            _state.SelectedModel = ModelBox.Text;
            if (!_workspace.SubmitPrepareDialog(_state))
            {
                ShowError(_state.Error ?? string.Empty);
                return;
            }
        }

        ResetModels();
        base.OnButtonClick(button);
    }
}
