param(
    [switch]$NoStart
)
$ErrorActionPreference = 'Continue'
Get-Process RetryProxy -ErrorAction SilentlyContinue | Where-Object { $_.Path -like 'D:\RetryProxy*' } | ForEach-Object { $_.Kill(); $_.WaitForExit(8000) }
Start-Sleep -Milliseconds 500
if (-not $NoStart) {
    $exe = 'D:\RetryProxy\src\RetryProxy.App\bin\Debug\net9.0-windows10.0.22621.0\RetryProxy.exe'
    Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe)
    Start-Sleep -Seconds 6
}
