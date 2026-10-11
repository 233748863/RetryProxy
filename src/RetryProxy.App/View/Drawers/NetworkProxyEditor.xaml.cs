using RetryProxy.Core.Config;
using System.Windows;
using System.Windows.Controls;

namespace RetryProxy.View.Drawers;

public partial class NetworkProxyEditor : UserControl
{
    public NetworkProxyEditor() => InitializeComponent();

    public void Load(NetworkProxySettings settings, bool keyLevel)
    {
        DrawerText.Bind(InheritText, TextBlock.TextProperty, keyLevel ? "沿用供应商联网设置" : "跟随系统联网设置");
        AddressBox.Text = settings.Address;
        UsernameBox.Text = settings.Username;
        PasswordBox.Password = settings.Password;
        ModeBox.SelectedIndex = (int)settings.Mode;
    }

    public string CaptureState() => DrawerText.Snapshot(new
    {
        Mode = ModeBox.SelectedIndex, Address = AddressBox.Text, Username = UsernameBox.Text, Password = PasswordBox.Password,
    });

    public string? Read(out NetworkProxySettings settings)
    {
        settings = new NetworkProxySettings
        {
            Mode = (NetworkProxyMode)ModeBox.SelectedIndex, Address = AddressBox.Text, Username = UsernameBox.Text, Password = PasswordBox.Password,
        }.Normalize();
        try { settings.Validate(); }
        catch (ConfigException error) { return error.Message; }
        return null;
    }

    public void ClearCredentials() { UsernameBox.Clear(); PasswordBox.Clear(); }

    private void ModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CustomPanel is not null) CustomPanel.Visibility = ModeBox.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }
}
