using System.Collections.Generic;

namespace RetryProxy.Core.Config;

/// <summary>
/// 代理运行配置。M0 阶段仅为占位，字段随 M1 按 PRD 补齐。
/// </summary>
public class ProxyConfig
{
    /// <summary>
    /// 监听端口起始值（每个通道递增）。
    /// </summary>
    public int BasePort { get; set; } = 8080;

    /// <summary>
    /// 通道列表。
    /// </summary>
    public List<ChannelConfig> Channels { get; set; } = [];
}

/// <summary>
/// 单个上游通道配置占位。
/// </summary>
public class ChannelConfig
{
    public string Name { get; set; } = string.Empty;

    public string Upstream { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}
