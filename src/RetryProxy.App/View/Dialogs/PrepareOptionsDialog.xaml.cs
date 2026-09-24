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
using RetryProxy.ViewModel;
using Wpf.Ui.Controls;

namespace RetryProxy.View.Dialogs;

/// <summary>独立准备选项窗：自行选择客户端及本机或手动配置的供应商。</summary>
public partial class PrepareOptionsDialog : ContentDialog
{
    private readonly PreparationDialogState _state;
    private readonly PreparationWorkspace _workspace;
    private bool _initialized;
    private CancellationTokenSource? _modelFetch;

    public PrepareOptionsDialog(ContentDialogHost? host, PreparationWorkspace workspace, PreparationDialogState state)
        : base(host)
    {
        _workspace = workspace;
        _state = state;
        InitializeComponent();
        ModelBox.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(OnModelTextChanged));
        ModelBox.Text = state.SelectedModel ?? string.Empty;
        IdleMinutesBox.Text = state.IdleMinutes;
        ProviderUrlBox.Text = state.ProviderUrl;
        ApiKeyBox.Password = state.ApiKey;
        CurrentRadio.IsChecked = state.Mode == PrepareMode.LocalProvider;
        NewRadio.IsChecked = state.Mode == PrepareMode.CustomProvider;
        CodexRadio.IsChecked = state.ClientType == ClientType.Codex;
        ClaudeRadio.IsChecked = state.ClientType == ClientType.Claude;
        _initialized = true;
        Unloaded += (_, _) => ResetModels();
        UpdateMode();
        UpdateProvider();
        UpdateReasoningEfforts();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // ContentDialog 的宽度属性只设上限；固定模板根容器，避免客户端说明文字带动弹窗伸缩。
        // 宿主空间不足时随可用宽度收缩，保留模板自带的边距和纵向滚动。
        if (GetTemplateChild("DialogRootGrid") is FrameworkElement dialogRoot)
        {
            var preferredWidth = DialogMaxWidth + DialogMargin.Left + DialogMargin.Right;
            dialogRoot.SetCurrentValue(WidthProperty, Math.Min(preferredWidth, availableSize.Width));
        }
        return base.MeasureOverride(availableSize);
    }

    private void UpdateMode()
    {
        _state.Mode = NewRadio.IsChecked == true ? PrepareMode.CustomProvider : PrepareMode.LocalProvider;
        PrimaryButtonText = I18nService.Instance.Translate("开始后台准备");
    }

    private void UpdateProvider()
    {
        SubtitleText.Text = $"使用 {_state.ClientType.Label()} 独立准备，成功后按设定间隔自动保活。";
        NewProviderPanel.Visibility = _state.Mode == PrepareMode.CustomProvider ? Visibility.Visible : Visibility.Collapsed;
        NewProviderKeyPanel.Visibility = _state.Mode == PrepareMode.CustomProvider ? Visibility.Visible : Visibility.Collapsed;
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

    private void UpdateReasoningEfforts()
    {
        if (!_state.ReasoningEffort.IsSupportedBy(_state.ClientType))
        {
            _state.ReasoningEffort = ReasoningEffort.Default;
        }
        ReasoningEffortBox.ItemsSource = ReasoningEffortExtensions.AvailableFor(_state.ClientType)
            .Select(effort => new PickerItem(effort.AsStr(), effort.Label())).ToArray();
        ReasoningEffortBox.SelectedValue = _state.ReasoningEffort.AsStr();
    }

    private void OnReasoningEffortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized && ReasoningEffortBox.SelectedValue is string value)
        {
            _state.ReasoningEffort = ReasoningEffortExtensions.Parse(value);
            ClearError();
        }
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
        _state.ProviderUrl = ProviderUrlBox.Text;
        _state.ApiKey = ApiKeyBox.Password;
        ResetModels();
        ClearError();

        ProviderEndpoint provider;
        string apiKey;
        try
        {
            var credential = _workspace.ResolveCredential(_state);
            provider = new ProviderEndpoint("独立准备", credential.BaseUrl);
            apiKey = credential.ApiKey;
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
            var models = await ProviderModelFetcher.FetchAsync(provider, apiKey, _state.ClientType, cancellation.Token);
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

    private void OnClientChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        _state.ClientType = ClaudeRadio.IsChecked == true ? ClientType.Claude : ClientType.Codex;
        UpdateReasoningEfforts();
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
            _state.ProviderUrl = ProviderUrlBox.Text;
            if (_state.Mode == PrepareMode.CustomProvider)
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
