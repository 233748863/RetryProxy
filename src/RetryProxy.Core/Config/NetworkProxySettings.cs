using System;
using System.Linq;
using System.Text;

namespace RetryProxy.Core.Config;

public enum NetworkProxyMode { Inherit, Direct, Custom }

/// <summary>供应商继承系统，Key 继承供应商；非指定模式不保存闲置的代理凭据。</summary>
public sealed class NetworkProxySettings : IEquatable<NetworkProxySettings>
{
    public NetworkProxyMode Mode { get; set; }
    public string Address { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    internal void NormalizeInPlace()
    {
        if (Mode != NetworkProxyMode.Custom) { Address = Username = Password = string.Empty; return; }
        Address = Address.Trim().TrimEnd('/');
        if (Address.Length > 0 && !Address.Contains("://", StringComparison.Ordinal)) Address = "http://" + Address;
    }

    public NetworkProxySettings Normalize() { var result = Clone(); result.NormalizeInPlace(); return result; }

    public void Validate()
    {
        if (!Enum.IsDefined(Mode)) throw new ConfigException("请选择有效的联网方式");
        if (Mode != NetworkProxyMode.Custom) return;
        if (Address.Length > 2048 || Address.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            || !Uri.TryCreate(Address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "socks5" or "socks5h")
            || uri.Host.Length == 0 || uri.Port is < 1 or > 65535 || uri.AbsolutePath is not ("" or "/") || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ConfigException("代理地址需为 HTTP 或 SOCKS5 地址，包含有效端口且不带路径，最多 2048 个字符");
        if (uri.UserInfo.Length > 0) throw new ConfigException("代理账号请填写在用户名和密码栏，勿放入地址");
        if (Username.Contains(':') || Username.Any(char.IsControl) || Password.Any(char.IsControl)
            || Encoding.UTF8.GetByteCount(Username) > 255 || Encoding.UTF8.GetByteCount(Password) > 255)
            throw new ConfigException("代理用户名和密码各最多 255 字节，不含控制字符；用户名不能包含冒号");
        if (Username.Length == 0 && Password.Length > 0) throw new ConfigException("填写代理密码时也需填写用户名");
    }

    internal Uri ProxyUri()
    {
        Validate();
        var builder = new UriBuilder(Address);
        if (Username.Length > 0)
        {
            builder.UserName = Uri.EscapeDataString(Username);
            builder.Password = Uri.EscapeDataString(Password);
        }
        return builder.Uri;
    }

    public static NetworkProxySettings Effective(NetworkProxySettings provider, NetworkProxySettings? key) =>
        (key is { Mode: not NetworkProxyMode.Inherit } ? key : provider).Normalize();

    public NetworkProxySettings Clone() => new() { Mode = Mode, Address = Address, Username = Username, Password = Password };
    public bool Equals(NetworkProxySettings? other) => other is not null && Mode == other.Mode && Address == other.Address
        && Username == other.Username && Password == other.Password;
    public override bool Equals(object? obj) => Equals(obj as NetworkProxySettings);
    public override int GetHashCode() => HashCode.Combine(Mode, Address, Username, Password);
    public override string ToString() => $"NetworkProxySettings {{ Mode = {Mode} }}";
}
