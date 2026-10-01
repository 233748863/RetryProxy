using RetryProxy.Service.I18n;
using System.Threading.Tasks;
using System.Windows;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace RetryProxy.Service;

/// <summary>删除确认入口；编辑表单统一使用右侧抽屉。</summary>
public sealed class Dialogs
{
    private readonly IContentDialogService _dialogs;

    public Dialogs(IContentDialogService dialogs) => _dialogs = dialogs;

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
}
