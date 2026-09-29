using RetryProxy.Core.Config;
using RetryProxy.Core.Workspace;
using RetryProxy.Service.I18n;
using RetryProxy.ViewModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace RetryProxy.View.Dialogs;

/// <summary>新增/编辑服务商。客户端只在新增时可选；校验失败时错误留在窗内，不关闭。</summary>
public partial class ProviderEditorDialog : ContentDialog
{
    private static readonly PickerItem[] ClientItems =
    {
        new(ClientType.Codex.AsStr(), ClientType.Codex.Label()),
        new(ClientType.Claude.AsStr(), ClientType.Claude.Label()),
    };

    private readonly ProviderEditor _editor;
    private readonly ProxyWorkspace _workspace;

    public ProviderEditorDialog(ContentDialogHost? host, ProxyWorkspace workspace, ProviderEditor editor)
        : base(host)
    {
        _workspace = workspace;
        _editor = editor;
        InitializeComponent();
        if (editor.IsEditing)
        {
            Title = I18nService.Instance.Translate("编辑服务商");
        }

        ClientBox.ItemsSource = ClientItems;
        ClientBox.SelectedItem = ClientItems.First(item => item.Key == editor.ClientType.AsStr());
        ClientBox.IsEnabled = !editor.IsEditing;
        NameBox.Text = editor.Name;
        UrlBox.Text = editor.Url;
    }

    protected override void OnButtonClick(ContentDialogButton button)
    {
        if (button == ContentDialogButton.Primary)
        {
            _editor.Name = NameBox.Text;
            _editor.Url = UrlBox.Text;
            if (!_editor.IsEditing && ClientBox.SelectedItem is PickerItem item)
            {
                _editor.ClientType = item.Key == ClientType.Claude.AsStr() ? ClientType.Claude : ClientType.Codex;
            }

            if (_workspace.CommitProvider(_editor) is { } error)
            {
                ErrorText.Text = error;
                ErrorText.Visibility = Visibility.Visible;
                return;
            }
        }

        base.OnButtonClick(button);
    }
}
