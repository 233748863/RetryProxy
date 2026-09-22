@echo off
setlocal
rem LLM Retry Proxy needs two .NET 9 runtimes (framework-dependent build):
rem   Microsoft.WindowsDesktop.App 9.x  (".NET Desktop Runtime 9")
rem   Microsoft.AspNetCore.App 9.x      ("ASP.NET Core Runtime 9")
rem Download: https://dotnet.microsoft.com/download/dotnet/9.0
set DESKTOP=0
set ASPNET=0
where dotnet >nul 2>nul
if errorlevel 1 goto :nodotnet
dotnet --list-runtimes 2>nul | findstr /r /c:"^Microsoft.WindowsDesktop.App 9\." >nul && set DESKTOP=1
dotnet --list-runtimes 2>nul | findstr /r /c:"^Microsoft.AspNetCore.App 9\." >nul && set ASPNET=1
if "%DESKTOP%"=="1" echo [OK] .NET Desktop Runtime 9
if "%DESKTOP%"=="0" echo [X]  .NET Desktop Runtime 9 is missing
if "%ASPNET%"=="1" echo [OK] ASP.NET Core Runtime 9
if "%ASPNET%"=="0" echo [X]  ASP.NET Core Runtime 9 is missing
if "%DESKTOP%%ASPNET%"=="11" goto :ok
echo Download both x64 runtimes from https://dotnet.microsoft.com/download/dotnet/9.0
echo The "ASP.NET Core Runtime 9 Hosting Bundle" installs the ASP.NET part; the Desktop Runtime is a separate installer.
exit /b 1
:nodotnet
echo [X] dotnet was not found. Install ".NET Desktop Runtime 9" and "ASP.NET Core Runtime 9" (x64).
echo     https://dotnet.microsoft.com/download/dotnet/9.0
exit /b 1
:ok
echo All runtimes are present. Start RetryProxy.exe.
exit /b 0
