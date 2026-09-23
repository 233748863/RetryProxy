using System;
using System.Security.Cryptography;
using RetryProxy.Core.Workspace;
using Xunit;

namespace RetryProxy.Tests;

public class PreparationSecretTests
{
    [Fact]
    public void SavedKeyIsEncryptedAndTiedToItsRoute()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var protectedKey = PreparationSecret.Protect("sk-route-secret", "route-a");
        Assert.DoesNotContain("sk-route-secret", protectedKey);
        Assert.Equal("sk-route-secret", PreparationSecret.Unprotect(protectedKey, "route-a"));
        Assert.Throws<CryptographicException>(() => PreparationSecret.Unprotect(protectedKey, "route-b"));
    }
}
