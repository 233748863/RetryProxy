param(
    [switch]$SkipTests,
    [string]$OutDir = (Join-Path $PSScriptRoot '..\dist')
)
# 发布：框架依赖 + 单文件 + win-x64，产物 dist\RetryProxy.exe（约 50 MB，含 WPF-UI 与 Windows SDK 投影）。
# 运行需要 .NET 9 Desktop Runtime 与 ASP.NET Core Runtime 9，dist\runtime-check.cmd 可检查。
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$app = Join-Path $root 'src\RetryProxy.App\RetryProxy.App.csproj'
$tests = Join-Path $root 'src\RetryProxy.Tests\RetryProxy.Tests.csproj'
$OutDir = [IO.Path]::GetFullPath($OutDir)

if (-not $SkipTests) {
    Write-Host '== dotnet test'
    dotnet test $tests -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "tests failed ($LASTEXITCODE)" }
}

Write-Host "== dotnet publish -> $OutDir"
if (Test-Path -LiteralPath $OutDir) {
    # 只清理上次发布的产物，保留用户可能放在 dist 下的 logs/User。
    Get-ChildItem -LiteralPath $OutDir -File | Where-Object { $_.Name -ne 'runtime-check.cmd' } | Remove-Item -Force
}
# 注意：某些 SDK 版本下 `--self-contained false` 会被忽略而发布成自包含（200 MB+），这里用显式属性。
$publishArgs = @('publish', $app, '-c', 'Release', '--nologo',
    '-p:RuntimeIdentifier=win-x64', '-p:SelfContained=false', '-p:PublishSingleFile=true',
    '-p:PublishReadyToRun=false', '-p:DebugType=embedded', '-o', $OutDir)
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "publish failed ($LASTEXITCODE)" }

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'runtime-check.cmd') -Destination (Join-Path $OutDir 'runtime-check.cmd') -Force
Get-ChildItem -LiteralPath $OutDir -Filter '*.pdb' | Remove-Item -Force
$exe = Join-Path $OutDir 'RetryProxy.exe'
$size = (Get-Item -LiteralPath $exe).Length
Write-Host ("== {0}  {1:N1} MB" -f $exe, ($size / 1MB))
if ($size -gt 120MB) { throw 'RetryProxy.exe is larger than 120 MB: the publish became self-contained, check SelfContained' }
Get-ChildItem -LiteralPath $OutDir | Select-Object Name, Length | Format-Table -AutoSize
