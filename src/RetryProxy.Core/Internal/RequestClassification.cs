using System;

namespace RetryProxy.Core.Internal;

internal static class RequestClassification
{
    /// <summary>模型清单查询：允许供应商路径前缀、尾斜杠及查询串；不包含单模型详情或 POST 请求。</summary>
    public static bool IsModelList(string method, string path)
    {
        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)) return false;
        var query = path.IndexOf('?');
        var cleanPath = query < 0 ? path.AsSpan() : path.AsSpan(0, query);
        return cleanPath.TrimEnd('/').EndsWith("/models", StringComparison.Ordinal);
    }
}
