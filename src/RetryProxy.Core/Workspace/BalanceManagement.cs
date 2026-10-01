using RetryProxy.Core.Balance;
using RetryProxy.Core.Config;

namespace RetryProxy.Core.Workspace;

public sealed partial class ProxyWorkspace
{
    /// <summary>
    /// 后台识别只保存查询方式，不改变当前供应商、通道或保活会话。
    /// 例如查询开始后用户换了地址或 Key，旧请求的识别结果直接丢弃。
    /// </summary>
    public string? RememberBalanceDetection(BalanceRequest request, BalanceQueryMode mode)
    {
        if (mode is not (BalanceQueryMode.Usage or BalanceQueryMode.UserBalance or BalanceQueryMode.OpenAiBilling))
            return "余额识别方式无效";
        var provider = Config.ProviderById(request.Key.ProviderId);
        if (provider is null || provider.ClientType != request.ClientType || provider.BaseUrl != request.BaseUrl
            || provider.KeyById(request.Key.KeyId)?.ApiKey != request.ApiKey
            || provider.BalanceQuery.Mode != BalanceQueryMode.Auto || request.Query.Mode != BalanceQueryMode.Auto)
            return null;
        if (provider.BalanceQuery.Detected == mode) return null;
        var candidate = Config.Clone();
        candidate.ProviderById(provider.Id)!.BalanceQuery.Detected = mode;
        if (SaveCandidate(candidate) is not null) return "已查询余额，但识别方式保存失败";
        _uiNotifier?.Invoke();
        return null;
    }
}
