using RetryProxy.Core.Config;
using RetryProxy.Service.I18n;
using System;
using System.Threading.Tasks;

namespace RetryProxy.Service;

public sealed record KeySwitchNotice(string Message, Action? Undo);

/// <summary>页面与托盘共用切换入口。撤销只保留最后一次切换，5 秒后失效。</summary>
public sealed class KeySwitchService(WorkspaceService workspace)
{
    private long _version;
    public event Action<KeySwitchNotice>? Switched;

    public void Switch(string routeId, string providerId, string keyId)
    {
        var config = workspace.Workspace.Config;
        var route = config.Routes.Find(item => item.Id == routeId);
        if (route is null || (route.CurrentProviderId == providerId && route.CurrentKeyId == keyId)) return;
        var previousProvider = route.CurrentProviderId;
        var previousKey = route.CurrentKeyId;
        if (workspace.Workspace.SwitchKey(routeId, providerId, keyId) is { } error)
        {
            workspace.Workspace.Notice = error;
            return;
        }

        var version = ++_version;
        var expires = DateTime.UtcNow.AddSeconds(5);
        var provider = workspace.Workspace.Config.ProviderById(providerId);
        var label = provider?.KeyById(keyId) is { } key ? $"{provider.Name} · {key.Name}" : provider?.Name;
        var i18n = I18nService.Instance;
        var message = string.Format(i18n.Translate("{0} 已切换到 {1}"), route.ClientType.Label(), label);
        if (workspace.Workspace.LastSwitchResentCount > 0)
            message += string.Format(i18n.Translate("，{0} 个请求改用新 Key 重发"), workspace.Workspace.LastSwitchResentCount);

        // 初次添加以前没有 Key 时没有可撤销目标。连续 A→B→C 只允许撤销 C→B。
        Action? undo = config.ProviderById(previousProvider)?.KeyById(previousKey) is null ? null : () =>
        {
            if (version != _version || DateTime.UtcNow >= expires) return;
            var current = workspace.Workspace.Config.Routes.Find(item => item.Id == routeId);
            if (current?.CurrentProviderId != providerId || current.CurrentKeyId != keyId) return;
            Switch(routeId, previousProvider, previousKey);
        };
        workspace.Flush();
        Switched?.Invoke(new KeySwitchNotice(message, undo));
    }
}
