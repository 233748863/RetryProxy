using RetryProxy.Core.Config;
using RetryProxy.Core.Workspace;
using RetryProxy.Service.I18n;
using RetryProxy.ViewModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace RetryProxy.View.Dialogs;

/// <summary>新增/编辑通道。字段与校验文案沿用 Rust 版；失败时错误留在窗内。</summary>
public partial class RouteEditorDialog : ContentDialog
{
    private static readonly PickerItem[] ClientItems =
    {
        new(ClientType.Codex.AsStr(), ClientType.Codex.Label()),
        new(ClientType.Claude.AsStr(), ClientType.Claude.Label()),
    };

    private readonly RouteEditor _editor;
    private readonly ProxyWorkspace _workspace;

    public RouteEditorDialog(ContentDialogHost? host, ProxyWorkspace workspace, RouteEditor editor)
        : base(host)
    {
        _workspace = workspace;
        _editor = editor;
        InitializeComponent();
        if (editor.IsEditing)
        {
            Title = I18nService.Instance.Translate("编辑通道");
        }

        ProviderText.Text = $"{I18nService.Instance.Translate("所属服务商：")}{editor.Provider}";
        NameBox.Text = editor.Name;
        ClientBox.ItemsSource = ClientItems;
        ClientBox.SelectedItem = editor.ClientType is { } client ? ClientItems.First(item => item.Key == client.AsStr()) : null;
        PortBox.Text = editor.Port;
        RetriesBox.Text = editor.Retries;
        TimeoutBox.Text = editor.Timeout;
        GenerationTimeoutBox.Text = editor.GenerationTimeout;
        TotalTimeoutBox.Text = editor.TotalTimeout;
        BaseDelayBox.Text = editor.BaseDelay;
        MaxDelayBox.Text = editor.MaxDelay;
    }

    protected override void OnButtonClick(ContentDialogButton button)
    {
        if (button == ContentDialogButton.Primary)
        {
            _editor.Name = NameBox.Text;
            _editor.ClientType = ClientBox.SelectedItem is PickerItem item
                ? (item.Key == ClientType.Claude.AsStr() ? ClientType.Claude : ClientType.Codex)
                : null;
            _editor.Port = PortBox.Text;
            _editor.Retries = RetriesBox.Text;
            _editor.Timeout = TimeoutBox.Text;
            _editor.GenerationTimeout = GenerationTimeoutBox.Text;
            _editor.TotalTimeout = TotalTimeoutBox.Text;
            _editor.BaseDelay = BaseDelayBox.Text;
            _editor.MaxDelay = MaxDelayBox.Text;
            if (_workspace.CommitRoute(_editor) is { } error)
            {
                ErrorText.Text = error;
                ErrorText.Visibility = Visibility.Visible;
                return;
            }
        }

        base.OnButtonClick(button);
    }
}
