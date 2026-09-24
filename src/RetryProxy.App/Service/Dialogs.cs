using RetryProxy.Core.Workspace;
using RetryProxy.Service.I18n;
using RetryProxy.View.Dialogs;
using System.Threading.Tasks;
using System.Windows;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace RetryProxy.Service;

/// <summary>通道管理与独立准备共用的对话框入口（WPF-UI ContentDialog）。</summary>
public sealed class Dialogs
{
    private readonly IContentDialogService _dialogs;
    private readonly WorkspaceService _workspaceService;

    public Dialogs(IContentDialogService dialogs, WorkspaceService workspaceService)
    {
        _dialogs = dialogs;
        _workspaceService = workspaceService;
    }

    private ProxyWorkspace Workspace => _workspaceService.Workspace;

    /// <summary>删除确认：`确认删除`（红）/ `取消`。</summary>
    public async Task<bool> ConfirmDeleteAsync(string title, string body)
    {
        var dialog = new ContentDialog(_dialogs.GetDialogHostEx())
        {
            Title = I18nService.Instance.Translate(title),
            Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, MaxWidth = 400 },
            PrimaryButtonText = I18nService.Instance.Translate("确认删除"),
            PrimaryButtonAppearance = ControlAppearance.Danger,
            CloseButtonText = I18nService.Instance.Translate("取消"),
            DefaultButton = ContentDialogButton.Close,
            DialogMaxWidth = 460,
        };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    public async Task ShowProviderEditorAsync(ProviderEditor editor)
    {
        var dialog = new ProviderEditorDialog(_dialogs.GetDialogHostEx(), Workspace, editor);
        await dialog.ShowAsync();
        _workspaceService.Flush();
    }

    public async Task ShowRouteEditorAsync(RouteEditor editor)
    {
        var dialog = new RouteEditorDialog(_dialogs.GetDialogHostEx(), Workspace, editor);
        await dialog.ShowAsync();
        _workspaceService.Flush();
    }

    public async Task ShowPrepareOptionsAsync(PreparationDialogState state)
    {
        var dialog = new PrepareOptionsDialog(_dialogs.GetDialogHostEx(), _workspaceService.Preparations, state);
        await dialog.ShowAsync();
        _workspaceService.Flush();
    }
}
