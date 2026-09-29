using RetryProxy.Core.Config;
using RetryProxy.Core.Workspace;
using RetryProxy.Service.I18n;
using RetryProxy.ViewModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace RetryProxy.View.Dialogs;

/// <summary>编辑通道。名称与客户端固定只读；服务商取页面上选中的那个，保存后通道改用它。失败时错误留在窗内。</summary>
public partial class RouteEditorDialog : ContentDialog
{
    private readonly RouteEditor _editor;
    private readonly ProxyWorkspace _workspace;

    public RouteEditorDialog(ContentDialogHost? host, ProxyWorkspace workspace, RouteEditor editor)
        : base(host)
    {
        _workspace = workspace;
        _editor = editor;
        InitializeComponent();
        var i18n = I18nService.Instance;
        ProviderText.Text = editor.ProviderChanged
            ? $"{i18n.Translate("保存后本通道改用服务商：")}{editor.ProviderName}"
            : $"{i18n.Translate("所属服务商：")}{editor.ProviderName}";
        NameBox.Text = editor.Name;
        ClientText.Text = editor.ClientType.Label();
        PortBox.Text = editor.Port;
        RetriesBox.Text = editor.Retries;
        TimeoutBox.Text = editor.Timeout;
        GenerationTimeoutBox.Text = editor.GenerationTimeout;
        TotalTimeoutBox.Text = editor.TotalTimeout;
        BaseDelayBox.Text = editor.BaseDelay;
        MaxDelayBox.Text = editor.MaxDelay;
        PassThroughCompressionSwitch.IsChecked = editor.PassThroughCompression;
    }

    protected override void OnButtonClick(ContentDialogButton button)
    {
        if (button == ContentDialogButton.Primary)
        {
            _editor.Port = PortBox.Text;
            _editor.Retries = RetriesBox.Text;
            _editor.Timeout = TimeoutBox.Text;
            _editor.GenerationTimeout = GenerationTimeoutBox.Text;
            _editor.TotalTimeout = TotalTimeoutBox.Text;
            _editor.BaseDelay = BaseDelayBox.Text;
            _editor.MaxDelay = MaxDelayBox.Text;
            _editor.PassThroughCompression = PassThroughCompressionSwitch.IsChecked == true;
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
