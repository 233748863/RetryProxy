using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using RetryProxy.Core.Config;
using RetryProxy.View.Drawers;
using Xunit;

namespace RetryProxy.App.Tests;

public sealed class NetworkProxyEditorTests
{
    [Fact]
    public void TogglingModesPreservesDraftCredentialsAndInactiveSaveClearsThem() => Run(() =>
    {
        var editor = new NetworkProxyEditor();
        editor.Load(new NetworkProxySettings { Mode = NetworkProxyMode.Custom, Address = "http://127.0.0.1:7897", Username = "user", Password = "private" }, true);
        var mode = (ComboBox)editor.FindName("ModeBox");
        mode.SelectedIndex = 1;
        Assert.Equal(Visibility.Collapsed, ((StackPanel)editor.FindName("CustomPanel")).Visibility);
        Assert.Null(editor.Read(out var direct));
        Assert.Empty(direct.Password);
        mode.SelectedIndex = 2;
        Assert.Null(editor.Read(out var custom));
        Assert.Equal("private", custom.Password);
        ((TextBox)editor.FindName("AddressBox")).Text = "http://user:password@proxy.test";
        Assert.NotNull(editor.Read(out _));
        editor.ClearCredentials();
        Assert.Empty(((PasswordBox)editor.FindName("PasswordBox")).Password);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProviderAndKeyEditorsTrackNetworkChangesInIsolatedDrafts(bool keyLevel) => Run(() =>
    {
        var key = new ProviderKey { Id = "k", Name = "k", ApiKey = "sk-fixture" };
        var provider = new ProviderEndpoint { Id = "p", Name = "p", BaseUrl = "https://fixture.example", Keys = new() { key } };
        using DrawerPage drawer = keyLevel ? new KeyDrawer(provider, key, false, true) : new ProviderDrawer(provider, false);
        Assert.False(drawer.HasChanges);
        var editor = (NetworkProxyEditor)drawer.FindName("NetworkEditor");
        ((ComboBox)editor.FindName("ModeBox")).SelectedIndex = 2;
        ((TextBox)editor.FindName("AddressBox")).Text = "127.0.0.1:7897";
        ((TextBox)editor.FindName("UsernameBox")).Text = "user";
        ((PasswordBox)editor.FindName("PasswordBox")).Password = "password";
        Assert.True(drawer.HasChanges);
        Assert.Null(keyLevel ? ((KeyDrawer)drawer).ReadDraft() : ((ProviderDrawer)drawer).ReadDraft());
        var settings = keyLevel ? ((KeyDrawer)drawer).Draft.NetworkProxy : ((ProviderDrawer)drawer).Draft.NetworkProxy;
        Assert.Equal("http://127.0.0.1:7897", settings.Address);
        Assert.Equal("password", settings.Password);
        Assert.Equal(NetworkProxyMode.Inherit, provider.NetworkProxy.Mode);
        Assert.Equal(NetworkProxyMode.Inherit, key.NetworkProxy.Mode);
    });

    private static void Run(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { test(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = true; thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
