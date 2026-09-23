using RetryProxy.Core.Config;
using RetryProxy.Core.Workspace;
using RetryProxy.Service.I18n;
using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using RetryProxy.Core.Cli;
using Wpf.Ui.Controls;

namespace RetryProxy.View.Dialogs;

/// <summary>后台准备选项窗：本通道供应商或内存中的新供应商。</summary>
public partial class PrepareOptionsDialog : ContentDialog
{
    private readonly PreparationDialogState _state;
    private readonly ProxyWorkspace _workspace;
    private readonly ProxyRoute _route;
    private bool _initialized;
    private CancellationTokenSource? _modelFetch;

    public PrepareOptionsDialog(ContentDialogHost? host, ProxyWorkspace workspace, PreparationDialogState state)
        : base(host)
    {
        _workspace = workspace;
        _state = state;
        _route = workspace.Config.Routes.First(route => route.Id == state.RouteId);
        InitializeComponent();
        ModelBox.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(OnModelTextChanged));
        ModelBox.Text = state.SelectedModel ?? string.Empty;
        IdleMinutesBox.Text = state.IdleMinutes;
        SubtitleText.Text = $"使用 {_route.ClientType.Label()} 在后台为“{_route.Name}”准备；新供应商仅存于本次运行，不修改现有配置。";
        NewProviderUrlBox.Text = state.NewProviderUrl;
        ApiKeyBox.Password = state.ApiKey;
        CurrentRadio.IsChecked = state.Mode == PrepareMode.CurrentProvider;
        NewRadio.IsChecked = state.Mode == PrepareMode.NewProvider;
        NewCodexRadio.IsChecked = state.NewProviderClientType == ClientType.Codex;
        NewClaudeRadio.IsChecked = state.NewProviderClientType == ClientType.Claude;
        _initialized = true;
        Unloaded += (_, _) => ResetModels();
        UpdateMode();
        UpdateProvider();
    }

    private void UpdateMode()
    {
        _state.Mode = NewRadio.IsChecked == true ? PrepareMode.NewProvider : PrepareMode.CurrentProvider;
        PrimaryButtonText = I18nService.Instance.Translate("开始后台准备");
    }

    private void UpdateProvider()
    {
        SubtitleText.Text = _state.Mode == PrepareMode.CurrentProvider
            ? $"使用本通道的 {_route.ClientType.Label()} 在后台准备；不修改现有配置。"
            : $"使用 {_state.NewProviderClientType.Label()} 为新供应商独立准备；不修改现有通道。";
        NewProviderPanel.Visibility = _state.Mode == PrepareMode.NewProvider ? Visibility.Visible : Visibility.Collapsed;
        NewProviderKeyPanel.Visibility = _state.Mode == PrepareMode.NewProvider ? Visibility.Visible : Visibility.Collapsed;
        FetchModelsButton.Visibility = Visibility.Visible;
        ModelsLoadingText.Visibility = Visibility.Collapsed;
        ModelBox.ItemsSource = _state.Models.Count > 0 ? _state.Models : null;
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
        _state.NewProviderUrl = NewProviderUrlBox.Text;
        _state.ApiKey = ApiKeyBox.Password;
        ResetModels();
        ClearError();

        ProviderEndpoint provider;
        string apiKey;
        try
        {
            if (_state.Mode == PrepareMode.CurrentProvider)
            {
                var current = _workspace.CurrentPreparationCredential(_state);
                provider = new ProviderEndpoint("本通道供应商", current.BaseUrl);
                apiKey = current.ApiKey;
            }
            else
            {
                provider = _workspace.PreparationTargetProvider(_state);
                apiKey = CliCredential.Create(_state.ApiKey, provider.BaseUrl).ApiKey;
            }
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
            var clientType = _state.Mode == PrepareMode.CurrentProvider ? _route.ClientType : _state.NewProviderClientType;
            var models = await ProviderModelFetcher.FetchAsync(provider, apiKey, clientType, cancellation.Token);
            if (!ReferenceEquals(_modelFetch, cancellation))
            {
                return;
            }

            _state.Models = models;
            _workspace.SetPreparationModels(_state, models);
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

    private void OnClientChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        _state.NewProviderClientType = NewClaudeRadio.IsChecked == true ? ClientType.Claude : ClientType.Codex;
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
            _state.NewProviderUrl = NewProviderUrlBox.Text;
            if (_state.Mode == PrepareMode.NewProvider)
            {
                _state.ApiKey = ApiKeyBox.Password;
            }
            _state.SelectedModel = ModelBox.Text;
            _state.IdleMinutes = IdleMinutesBox.Text;
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
