using RetryProxy.Core.Config;
using RetryProxy.Helpers.Ui;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace RetryProxy.View.Windows;

public partial class AboutWindow
{
    public AboutWindow()
    {
        InitializeComponent();
        SourceInitialized += (s, e) => WindowHelper.TryApplySystemBackdrop(this);
        VersionRun.Text = Global.Version;
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
