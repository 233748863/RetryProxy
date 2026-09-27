using System;
using System.Runtime.CompilerServices;
using RetryProxy.Core.Tls;

namespace RetryProxy.Tests.Support;

/// <summary>
/// Claude 通道连 https 上游时默认使用指纹，运行期间会后台运行本机 claude 抓取指纹。
/// 测试进程启动时把全局指纹库换成不抓取的实例（沿用内置指纹），避免任何用例启动真实的 Claude Code。
/// </summary>
internal static class TestFingerprintStore
{
    [ModuleInitializer]
    internal static void Install()
    {
        TlsFingerprintStore.Shared = new TlsFingerprintStore(_ => throw new TimeoutException("测试环境不抓取本机 Claude Code"));
    }
}
