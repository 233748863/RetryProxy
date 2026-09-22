using RetryProxy.Core.Workspace;
using RetryProxy.Service.I18n;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace RetryProxy.View.Dialogs;

/// <summary>新增/编辑服务商。校验失败时错误留在窗内，不关闭。</summary>
public partial class ProviderEditorDialog : ContentDialog
{
    private readonly ProviderEditor _editor;
    private readonly ProxyWorkspace _workspace;

    public ProviderEditorDialog(ContentPresenter? host, ProxyWorkspace workspace, ProviderEditor editor)
        : base(host)
    {
        _workspace = workspace;
        _editor = editor;
        InitializeComponent();
        if (editor.IsEditing)
        {
            Title = I18nService.Instance.Translate("编辑服务商");
        }

        NameBox.Text = editor.Name;
        UrlBox.Text = editor.Url;
    }

    protected override void OnButtonClick(ContentDialogButton button)
    {
        if (button == ContentDialogButton.Primary)
        {
            _editor.Name = NameBox.Text;
            _editor.Url = UrlBox.Text;
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
