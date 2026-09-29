using RetryProxy.Core.Config;
using RetryProxy.Service;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace RetryProxy.View.Controls;

/// <summary>四个页面共用已保存的客户端选择；只改变查看对象，不切换当前 Key。</summary>
public partial class ClientSelector : UserControl
{
    private WorkspaceService? _workspace;

    public ClientSelector()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _workspace = App.GetService<WorkspaceService>();
            if (_workspace is not null) _workspace.Refreshed += Refresh;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            if (_workspace is not null) _workspace.Refreshed -= Refresh;
        };
    }

    private void Refresh()
    {
        var claude = _workspace?.Workspace.SelectedClient == ClientType.Claude;
        ClaudeButton.Appearance = claude ? ControlAppearance.Primary : ControlAppearance.Transparent;
        CodexButton.Appearance = claude ? ControlAppearance.Transparent : ControlAppearance.Primary;
    }

    private void Select(ClientType client)
    {
        _workspace?.Workspace.SelectClient(client);
        _workspace?.Flush();
    }

    private void OnClaude(object sender, RoutedEventArgs e) => Select(ClientType.Claude);
    private void OnCodex(object sender, RoutedEventArgs e) => Select(ClientType.Codex);
}
