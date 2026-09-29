using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetryProxy.Core.Client;
using RetryProxy.Core.Config;
using RetryProxy.Service;
using RetryProxy.View.Drawers;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace RetryProxy.ViewModel.Pages;

public partial class ClientConnectionRowViewModel : ObservableObject
{
    private readonly WorkspaceService _workspace;
    private readonly ClientTakeoverService _clients;
    public ClientType Client { get; }
    public string Name => Client.Label();
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string _error = string.Empty;
    [ObservableProperty] private string _configPath = string.Empty;
    [ObservableProperty] private string _backupPath = string.Empty;
    [ObservableProperty] private string _takeoverText = string.Empty;
    [ObservableProperty] private bool _canTakeOver;
    [ObservableProperty] private bool _canCancel;

    public ClientConnectionRowViewModel(ClientType client, WorkspaceService workspace, ClientTakeoverService clients)
    {
        Client = client;
        _workspace = workspace;
        _clients = clients;
        workspace.Refreshed += Refresh;
        Refresh();
    }

    private void Refresh()
    {
        var info = _workspace.Clients.Connection(Client);
        Status = DrawerText.T(ClientTakeoverService.StatusText(info.Status));
        Error = DrawerText.Error(info.Error);
        ConfigPath = info.ConfigPath.Length == 0 ? ClientConfigPaths.Resolve(Client) : info.ConfigPath;
        BackupPath = info.BackupPath.Length == 0 ? Path.Combine(_clients.BackupRoot, Client.AsStr()) : info.BackupPath;
        TakeoverText = DrawerText.T(info.Status is ClientConnectionStatus.Modified or ClientConnectionStatus.Unavailable ? "重新接管" : "接管");
        var route = _workspace.Workspace.Config.RouteFor(Client)!;
        var enabled = _workspace.Workspace.Config.ClientTakeover[Client].Enabled;
        CanTakeOver = !ClientConfigPaths.WritesBlocked && (info.Status != ClientConnectionStatus.TakenOver || !enabled)
            && _workspace.Workspace.Config.CurrentKeyOf(route) is not null;
        CanCancel = !ClientConfigPaths.WritesBlocked && enabled;
    }

    [RelayCommand] private Task TakeOver() => _clients.TakeOverAsync(Client);
    [RelayCommand] private Task Cancel() => _clients.CancelTakeoverAsync(Client);
    [RelayCommand] private void OpenConfig() => OpenDirectory(Path.GetDirectoryName(ConfigPath)!);
    [RelayCommand] private void OpenBackup() => OpenDirectory(BackupPath);

    private void OpenDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
        }
        catch (Exception) { _workspace.Workspace.Notice = DrawerText.T("无法打开客户端配置或备份目录"); }
    }
}
