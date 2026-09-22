using System;
using System.IO;
using RetryProxy.Core.Config;

namespace RetryProxy.Helpers;

public class TempManager
{
    public static string GetTempDirectory()
    {
        var tmp = Global.Absolute("User/Temp");
        Directory.CreateDirectory(tmp);
        return tmp;
    }

    public static void CleanUp()
    {
        try
        {
            DirectoryHelper.DeleteDirectoryRecursively(GetTempDirectory());
        }
        catch
        {
            // Suppress any exceptions to avoid exposing errors
        }
    }
}