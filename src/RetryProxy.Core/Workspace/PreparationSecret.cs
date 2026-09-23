using System;
using System.Security.Cryptography;
using System.Text;

namespace RetryProxy.Core.Workspace;

internal static class PreparationSecret
{
    public static string Protect(string apiKey, string routeId)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var bytes = Encoding.UTF8.GetBytes(apiKey);
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(bytes, Encoding.UTF8.GetBytes(routeId), DataProtectionScope.CurrentUser));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static string Unprotect(string value, string routeId)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), Encoding.UTF8.GetBytes(routeId), DataProtectionScope.CurrentUser);
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
