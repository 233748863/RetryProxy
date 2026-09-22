using System;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;

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

            if (!connect && request.HttpRequestError == HttpRequestError.Unknown && HasInner<SocketException>(request))
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
            var connect = socket.SocketErrorCode is SocketError.ConnectionRefused
                or SocketError.HostUnreachable
                or SocketError.NetworkUnreachable
                or SocketError.HostNotFound
                or SocketError.TimedOut;
            return new UpstreamException(socket.Message, false, connect, socket);
        }

        return new UpstreamException(error.Message, false, false, error);
    }

    private static bool HasInner<T>(Exception error)
        where T : Exception
    {
        var current = error.InnerException;
        for (var depth = 0; depth < 16 && current is not null; depth++)
        {
            if (current is T)
            {
                return true;
            }

            current = current.InnerException;
        }

        return false;
    }
}

internal enum NetworkPhase
{
    AwaitingResponse,
    ReadingResponse,
}

/// <summary>网络错误的日志文案（对应 proxy.rs 的 network_error_label / network_cause_fields）。</summary>
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
                ? "建立上游连接超时"
                : phase == NetworkPhase.AwaitingResponse ? "收到上游响应前超时" : "读取上游响应超时";
        }
        else if (error.IsConnect)
        {
            kind = "ConnectError";
            reason = "建立上游连接失败";
        }
        else
        {
            kind = "ClientError";
            reason = phase == NetworkPhase.AwaitingResponse ? "发送请求或等待上游响应失败" : "读取上游响应失败";
        }

        var route = usingSystemProxy ? "系统代理" : "直连";
        return $"{reason}（{kind}），链路：{route}{CauseFields(error)}";
    }

    /// <summary>沿 InnerException 链找系统级网络错误，只记录结构化的类别与错误码，不带任何消息文本。</summary>
    public static string CauseFields(Exception error)
    {
        Exception? cause = null;
        Exception? current = error;
        for (var depth = 0; depth < 16 && current is not null; depth++)
        {
            if (current is SocketException || current is IOException)
            {
                cause = current;
            }

            current = current.InnerException;
        }

        if (cause is null)
        {
            return "，底层原因：未提供可识别的系统错误";
        }

        var fields = $"，底层原因：{Describe(cause)}";
        if (cause is SocketException socket)
        {
            fields += $"，系统错误码 {socket.NativeErrorCode}";
        }

        return fields;
    }

    private static string Describe(Exception cause)
    {
        if (cause is SocketException socket)
        {
            return socket.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => "连接被拒绝",
                SocketError.ConnectionReset => "连接被重置",
                SocketError.ConnectionAborted => "连接被中止",
                SocketError.TimedOut => "网络操作超时",
                SocketError.NotConnected => "连接未建立",
                SocketError.Shutdown => "连接已关闭",
                SocketError.AccessDenied => "系统拒绝网络访问",
                SocketError.AddressNotAvailable => "网络地址不可用",
                SocketError.AddressAlreadyInUse => "网络地址已被占用",
                SocketError.HostUnreachable => "目标主机不可达",
                SocketError.NetworkUnreachable => "目标网络不可达",
                _ => "系统网络错误",
            };
        }

        if (cause is HttpIOException http)
        {
            return http.HttpRequestError switch
            {
                HttpRequestError.ResponseEnded => "连接提前结束",
                HttpRequestError.InvalidResponse => "收到无法解析的网络数据",
                HttpRequestError.ConnectionError => "连接被中止",
                _ => "系统网络错误",
            };
        }

        if (cause is EndOfStreamException)
        {
            return "连接提前结束";
        }

        if (cause is InvalidDataException)
        {
            return "收到无法解析的网络数据";
        }

        return "系统网络错误";
    }
}
