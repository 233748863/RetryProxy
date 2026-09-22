using System;

namespace RetryProxy.Core.Config;

/// <summary>
/// 环境变量对当前选中通道的临时覆盖，不持久化。
/// </summary>
public sealed class RouteRuntimeOverrides : IEquatable<RouteRuntimeOverrides>
{
    public string RouteId { get; set; } = string.Empty;

    public string? UpstreamBaseUrl { get; set; }

    public int? ListenPort { get; set; }

    public long? MaxRetries { get; set; }

    public double? TimeoutSeconds { get; set; }

    public double? GenerationTimeoutSeconds { get; set; }

    public double? TotalTimeoutSeconds { get; set; }

    public double? BaseDelaySeconds { get; set; }

    public double? MaxDelaySeconds { get; set; }

    public RouteRuntimeOverrides Clone() => (RouteRuntimeOverrides)MemberwiseClone();

    public bool Equals(RouteRuntimeOverrides? other)
    {
        return other is not null
            && RouteId == other.RouteId
            && UpstreamBaseUrl == other.UpstreamBaseUrl
            && ListenPort == other.ListenPort
            && MaxRetries == other.MaxRetries
            && TimeoutSeconds == other.TimeoutSeconds
            && GenerationTimeoutSeconds == other.GenerationTimeoutSeconds
            && TotalTimeoutSeconds == other.TotalTimeoutSeconds
            && BaseDelaySeconds == other.BaseDelaySeconds
            && MaxDelaySeconds == other.MaxDelaySeconds;
    }

    public override bool Equals(object? obj) => Equals(obj as RouteRuntimeOverrides);

    public override int GetHashCode() => HashCode.Combine(RouteId, ListenPort, UpstreamBaseUrl);
}
