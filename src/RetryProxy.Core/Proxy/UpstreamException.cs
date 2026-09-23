using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;

namespace RetryProxy.Core.Proxy;

/// <summary>上游网络错误的分类（对应 reqwest::Error 的 is_timeout / is_connect）。</summary>
internal sealed class UpstreamException : Exception
{
    public UpstreamException(string message, bool isTimeout, bool isConnect, Exception? inner)
        : base(message, inner)
    {
        IsTimeout = isTimeout;
        IsConnect = isConnect;
    }

    public bool IsTimeout { get; }

    public bool IsConnect { get; }

    public static UpstreamException Timeout(bool connect, Exception? inner = null)
    {
        return new UpstreamException(connect ? "connect timeout" : "read timeout", true, connect, inner);
    }

    /// <summary>把 HttpClient / 流读取抛出的异常归类为上游网络错误。</summary>
    public static UpstreamException From(Exception error)
    {
        if (error is UpstreamException upstream)
        {
            return upstream;
        }

        if (error is HttpRequestException request)
        {
            var connect = request.HttpRequestError is HttpRequestError.ConnectionError
                or HttpRequestError.NameResolutionError
                or HttpRequestError.SecureConnectionError
                or HttpRequestError.ProxyTunnelError;
            if (HasInner<TimeoutException>(request))
            {
                return new UpstreamException(request.Message, true, true, request);
            }

            // 未分类的请求异常只有在套接字错误本身属于“连不上”时才算连接失败；
            // 等待响应途中被重置（10054）等情况要留在 ClientError，才能与真正的连接失败区分。
            if (!connect && request.HttpRequestError == HttpRequestError.Unknown
                && FindInner<SocketException>(request) is { } socketError && IsConnectFailure(socketError.SocketErrorCode))
            {
                connect = true;
            }

            return new UpstreamException(request.Message, false, connect, request);
        }

        if (error is TimeoutException)
        {
            return new UpstreamException(error.Message, true, false, error);
        }

        if (error is SocketException socket)
        {
            return new UpstreamException(socket.Message, false, IsConnectFailure(socket.SocketErrorCode), socket);
        }

        return new UpstreamException(error.Message, false, false, error);
    }

    private static bool IsConnectFailure(SocketError code)
    {
        return code is SocketError.ConnectionRefused
            or SocketError.HostUnreachable
            or SocketError.NetworkUnreachable
            or SocketError.HostNotFound
            or SocketError.TimedOut;
    }

    private static bool HasInner<T>(Exception error)
        where T : Exception
    {
        return FindInner<T>(error) is not null;
    }

    private static T? FindInner<T>(Exception error)
        where T : Exception
    {
        var current = error.InnerException;
        for (var depth = 0; depth < 16 && current is not null; depth++)
        {
            if (current is T match)
            {
                return match;
            }

            current = current.InnerException;
        }

        return null;
    }
}

internal enum NetworkPhase
{
    AwaitingResponse,
    ReadingResponse,
}

/// <summary>
/// 网络错误的日志文案（对应 proxy.rs 的 network_error_label / network_cause_fields）。
/// 正文用大白话只说"发生了什么"，不猜测原因；专业信息统一收进末尾的"（技术细节：…）"，
/// 且只含类型名、枚举名与数字，不带任何异常消息文本（消息里可能含 URL、密钥）。
/// </summary>
internal static class NetworkErrorLabel
{
    public static string Describe(UpstreamException error, bool usingSystemProxy, NetworkPhase phase)
    {
        string kind;
        string reason;
        if (error.IsTimeout)
        {
            kind = "Timeout";
            reason = error.IsConnect
                ? "连上游一直连不上，已等到超时"
                : phase == NetworkPhase.AwaitingResponse ? "上游一直没回复，已等到超时" : "上游回复到一半就没了动静，已等到超时";
        }
        else if (error.IsConnect)
        {
            kind = "ConnectError";
            reason = "连不上上游";
        }
        else
        {
            kind = "ClientError";
            reason = phase == NetworkPhase.AwaitingResponse ? "请求已发出，但没等到上游回复连接就断了" : "上游回复到一半，连接就断了";
        }

        var diagnosis = Diagnose(error);
        var route = usingSystemProxy ? "系统代理" : "直连";
        var cause = error.IsTimeout ? string.Empty : "，" + diagnosis.Plain;

        return $"{reason}{cause}，链路：{route}{TechnicalDetails(kind, diagnosis)}";
    }

    /// <summary>只根据异常链给出"，大白话原因（技术细节：…）"，供单元测试与不经过 Describe 的调用方使用。</summary>
    public static string CauseFields(Exception error)
    {
        var diagnosis = Diagnose(error);
        return $"，{diagnosis.Plain}{TechnicalDetails(null, diagnosis)}";
    }

    private static string TechnicalDetails(string? kind, Diagnosis diagnosis)
    {
        var parts = new List<string>();
        if (kind is not null)
        {
            parts.Add(kind);
        }

        if (diagnosis.Category is { } category)
        {
            parts.Add($"类别 {category}");
        }

        if (diagnosis.Code is { } code)
        {
            parts.Add($"错误码 {code}");
        }

        if (diagnosis.Chain.Count > 0)
        {
            parts.Add($"异常链 {string.Join(" > ", diagnosis.Chain)}");
        }

        return parts.Count == 0 ? string.Empty : $"（技术细节：{string.Join("；", parts)}）";
    }

    private sealed record Diagnosis(string Plain, HttpRequestError? Category, int? Code, IReadOnlyList<string> Chain);

    private static Diagnosis Diagnose(Exception error)
    {
        var chain = new List<Exception>();
        var current = error is UpstreamException upstream ? upstream.InnerException : error;
        for (var depth = 0; depth < 16 && current is not null; depth++)
        {
            chain.Add(current);
            current = current.InnerException;
        }

        Exception? cause = null;
        HttpRequestError? category = null;
        Win32Exception? win32 = null;
        var tlsFailure = false;
        var cancelled = false;
        foreach (var item in chain)
        {
            if (item is SocketException || item is IOException)
            {
                cause = item;
            }

            if (item is HttpRequestException request && request.HttpRequestError != HttpRequestError.Unknown)
            {
                category ??= request.HttpRequestError;
            }
            else if (item is HttpIOException httpIo && httpIo.HttpRequestError != HttpRequestError.Unknown)
            {
                category ??= httpIo.HttpRequestError;
            }

            win32 ??= item as Win32Exception;
            tlsFailure |= item is AuthenticationException;
            cancelled |= item is OperationCanceledException;
        }

        var plain = cause is not null
            ? DescribeCause(cause)
            : tlsFailure
                ? DescribeCategory(HttpRequestError.SecureConnectionError)
                : category is { } known
                    ? DescribeCategory(known)
                    : cancelled
                        ? "这次操作被取消了"
                        : chain.Count == 0
                            ? "没有更具体的原因"
                            : "原因程序认不出来";
        int? code = cause is SocketException socket ? socket.NativeErrorCode : win32?.NativeErrorCode;
        return new Diagnosis(plain, category, code, chain.Select(item => item.GetType().Name).ToList());
    }

    private static string DescribeCause(Exception cause)
    {
        if (cause is SocketException socket)
        {
            return socket.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => "对方拒绝了连接",
                SocketError.ConnectionReset => "连接被对方强行掐断",
                SocketError.ConnectionAborted => "连接中途被中止",
                SocketError.TimedOut => "网络长时间没有响应",
                SocketError.NotConnected => "连接还没建立起来",
                SocketError.Shutdown => "连接已经关闭",
                SocketError.AccessDenied => "系统不允许本程序联网",
                SocketError.AddressNotAvailable => "本机的网络地址不可用",
                SocketError.AddressAlreadyInUse => "网络端口已被别的程序占用",
                SocketError.HostUnreachable => "网络不通，到不了上游",
                SocketError.NetworkUnreachable => "网络不通，到不了上游",
                SocketError.HostNotFound => "查不到上游网址对应的服务器（域名解析失败）",
                SocketError.OperationAborted => "本机取消了这次网络操作",
                _ => "网络出了错",
            };
        }

        if (cause is HttpIOException http)
        {
            return DescribeCategory(http.HttpRequestError);
        }

        if (cause is EndOfStreamException)
        {
            return "对方主动关闭了连接";
        }

        if (cause is InvalidDataException)
        {
            return "上游回复的内容看不懂（格式不对）";
        }

        return "网络出了错";
    }

    /// <summary>.NET 请求错误类别的大白话；对端正常关闭（FIN）落在 ResponseEnded，与被掐断、超时可区分。</summary>
    private static string DescribeCategory(HttpRequestError error)
    {
        return error switch
        {
            HttpRequestError.ResponseEnded => "对方主动关闭了连接",
            HttpRequestError.InvalidResponse => "上游回复的内容看不懂（格式不对）",
            HttpRequestError.ConnectionError => "连接中途被中止",
            HttpRequestError.NameResolutionError => "查不到上游网址对应的服务器（域名解析失败）",
            HttpRequestError.SecureConnectionError => "加密连接（HTTPS）没建立起来",
            HttpRequestError.ProxyTunnelError => "系统代理没能帮忙连到上游",
            HttpRequestError.UserAuthenticationError => "代理或上游要求的身份验证没通过",
            HttpRequestError.HttpProtocolError => "上游的 HTTP 通信不符合规范",
            HttpRequestError.VersionNegotiationError => "和上游谈不拢用哪个 HTTP 版本",
            HttpRequestError.ExtendedConnectNotSupported => "上游不支持这种连接方式",
            HttpRequestError.ConfigurationLimitExceeded => "超出了本程序的连接数限制",
            _ => "网络出了错",
        };
    }
}
