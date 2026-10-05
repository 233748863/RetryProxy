$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'runtime_files.ps1')

function Assert-True([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
}

function Write-Fixture([string]$Path, [string]$Content) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Content)
}

function Assert-CopiedFiles([string]$Directory, [System.Collections.IDictionary]$Expected) {
    $actual = @(Get-ChildItem -LiteralPath $Directory -File -Recurse)
    Assert-True ($actual.Count -eq $Expected.Count) '目标文件数量不符，存在遗漏或多余复制。'
    foreach ($relativePath in $Expected.Keys) {
        $path = Join-Path $Directory $relativePath
        Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "运行文件缺失：$relativePath"
        Assert-True ([IO.File]::ReadAllText($path) -ceq $Expected[$relativePath]) "运行文件内容不符：$relativePath"
    }
}

# 所有文件均为工作树内的临时夹具，不读取程序产物、真实配置或用户目录。
$root = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $root ('.tmp/runtime-files-' + [guid]::NewGuid().ToString('N'))
try {
    $source = Join-Path $testRoot 'source'
    $destination = Join-Path $testRoot 'destination'
    $expected = [ordered]@{
        'RetryProxy.exe' = 'fixture-exe'
        'RetryProxy.Core.dll' = 'fixture-managed-dll'
        'RetryProxy.deps.json' = '{"fixture":"deps"}'
        'RetryProxy.runtimeconfig.json' = '{"fixture":"runtimeconfig"}'
        'runtimes/win-x64/native/e_sqlite3.dll' = 'fixture-native-sqlite'
        'runtimes/win-x64/lib/net9.0/provider.dll' = 'fixture-runtime-managed-dll'
    }
    foreach ($relativePath in $expected.Keys) {
        Write-Fixture (Join-Path $source $relativePath) $expected[$relativePath]
    }
    $privatePaths = @(
        'User/config.json', 'User/credential.dll', 'logs/retry-proxy.log', 'logs/private.dll',
        'config.json', 'RetryProxy.pdb', 'private.txt', 'other/secret.dll', 'RetryProxy.deps.json.bak'
    )
    foreach ($relativePath in $privatePaths) {
        Write-Fixture (Join-Path $source $relativePath) 'private-fixture'
    }

    # 开发构建必须带齐顶层运行文件和完整的 runtimes 子目录。
    $exePath = Join-Path $source 'RetryProxy.exe'
    Copy-RetryProxyRuntime -ExePath $exePath -DestinationDirectory $destination
    Assert-CopiedFiles $destination $expected

    # 即使个人目录中存在 DLL，也不得将这些目录或其他顶层文件带入目标。
    foreach ($relativePath in ($privatePaths + @('User', 'logs', 'other'))) {
        Assert-True (!(Test-Path -LiteralPath (Join-Path $destination $relativePath))) "误复制个人文件或目录：$relativePath"
    }

    # 第二次复制应覆盖已更新的依赖，同时保持原有目录层级。
    $expected['runtimes/win-x64/native/e_sqlite3.dll'] = 'fixture-native-sqlite-updated'
    Write-Fixture (Join-Path $source 'runtimes/win-x64/native/e_sqlite3.dll') $expected['runtimes/win-x64/native/e_sqlite3.dll']
    Copy-RetryProxyRuntime -ExePath $exePath -DestinationDirectory $destination
    Assert-CopiedFiles $destination $expected
    Assert-True (!(Test-Path -LiteralPath (Join-Path $destination 'runtimes/runtimes'))) '重复复制生成了 runtimes/runtimes。'

    # 只有 EXE 的单文件包也应正常复制，不要求存在 runtimes。
    $singleSource = Join-Path $testRoot 'single-source'
    $singleDestination = Join-Path $testRoot 'single-destination'
    $singleExe = Join-Path $singleSource 'RetryProxy.exe'
    Write-Fixture $singleExe 'fixture-single-exe'
    Copy-RetryProxyRuntime -ExePath $singleExe -DestinationDirectory $singleDestination
    Assert-CopiedFiles $singleDestination @{ 'RetryProxy.exe' = 'fixture-single-exe' }
    Assert-True (!(Test-Path -LiteralPath (Join-Path $singleDestination 'runtimes'))) '单文件包不应生成 runtimes 目录。'

    # EXE 缺失时必须明确失败，且不能先创建目标目录或复制旁边的文件。
    $missingDestination = Join-Path $testRoot 'missing-destination'
    $missingExeFailed = $false
    try {
        Copy-RetryProxyRuntime -ExePath (Join-Path $source 'missing.exe') -DestinationDirectory $missingDestination
    }
    catch {
        $missingExeFailed = $_.Exception.Message -like '程序文件不存在：*'
    }
    Assert-True $missingExeFailed 'EXE 缺失时未返回预期错误。'
    Assert-True (!(Test-Path -LiteralPath $missingDestination)) 'EXE 缺失时仍创建了目标目录。'

    Write-Output '运行文件复制测试通过：依赖完整、个人文件隔离、重复复制、单文件包、缺失 EXE，共 5 项。'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
