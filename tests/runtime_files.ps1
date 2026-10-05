function Copy-RetryProxyRuntime {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$ExePath,

        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$DestinationDirectory
    )

    if (!(Test-Path -LiteralPath $ExePath -PathType Leaf)) {
        throw "程序文件不存在：$ExePath"
    }

    $sourceDirectory = Split-Path -Parent (Resolve-Path -LiteralPath $ExePath -ErrorAction Stop).ProviderPath
    New-Item -ItemType Directory -Path $DestinationDirectory -Force -ErrorAction Stop | Out-Null

    # 只复制顶层运行文件，不带入用户配置、日志或调试文件。
    Get-ChildItem -LiteralPath $sourceDirectory -File -ErrorAction Stop |
        Where-Object { $_.Name -match '\.(exe|dll)$|\.(deps|runtimeconfig)\.json$' } |
        Copy-Item -Destination $DestinationDirectory -Force -ErrorAction Stop

    # 开发构建的 SQLite 原生依赖位于此目录；单文件发布可以没有此目录。
    $runtimesDirectory = Join-Path $sourceDirectory 'runtimes'
    if (Test-Path -LiteralPath $runtimesDirectory -PathType Container) {
        # 始终以目标根目录为目的地，重复复制时合并目录，避免 runtimes/runtimes。
        Copy-Item -LiteralPath $runtimesDirectory -Destination $DestinationDirectory -Recurse -Force -ErrorAction Stop
    }
}
