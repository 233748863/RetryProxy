# 测试用 Codex：只访问测试注入的本地准备服务，走完整握手、代理和用量回传。
$ErrorActionPreference = 'Stop'
[Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$preparationUrl = $null
$preparationModel = 'preparation-test-model'
foreach ($argument in $args) {
    if ($argument -match '^model_providers\.retry_proxy_prepare\.base_url=(.+)$') {
        $preparationUrl = ($Matches[1] | ConvertFrom-Json).TrimEnd('/') + '/responses'
    }
    if ($argument -match '^model=(.+)$') { $preparationModel = $Matches[1] | ConvertFrom-Json }
}
if (-not $preparationUrl -or -not ([Uri]$preparationUrl).IsLoopback) { throw 'Expected a local test preparation service' }

while ($null -ne ($line = [Console]::In.ReadLine())) {
    $message = $line | ConvertFrom-Json
    switch ($message.method) {
        'initialize' { $reply = @{ id = $message.id; result = @{} } }
        'initialized' { continue }
        'config/read' { $reply = @{ id = $message.id; result = @{ config = @{ mcp_servers = @{} } } } }
        'thread/start' { $reply = @{ id = $message.id; result = @{ thread = @{ id = 'preparation-test-thread' }; model = $preparationModel } } }
        'turn/start' {
            $marker = $message.params.responsesapiClientMetadata.retry_proxy_keepalive
            if (-not $marker) { throw 'Missing preparation marker' }
            $gate = $env:RETRY_PROXY_TEST_PREPARATION_GATE
            while ($gate -and -not (Test-Path -LiteralPath $gate)) { Start-Sleep -Milliseconds 20 }
            $body = @{
                model = $preparationModel; stream = $true; store = $false
                input = @(@{ role = 'user'; content = @(@{ type = 'input_text'; text = $message.params.input[0].text }) })
                client_metadata = @{ 'x-codex-turn-metadata' = (@{ retry_proxy_keepalive = $marker } | ConvertTo-Json -Compress) }
            } | ConvertTo-Json -Depth 10 -Compress
            $response = Invoke-WebRequest -UseBasicParsing $preparationUrl -Method Post -ContentType 'application/json' -Headers @{ Authorization = 'Bearer ' + $env:RETRY_PROXY_PREPARE_KEY } -Body ([Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 8
            if (-not $response.Content.Contains('response.completed')) { throw 'Incomplete fixture response' }
            [Console]::WriteLine((@{ id = $message.id; result = @{ turn = @{ id = 'test-turn'; status = 'inProgress'; items = @() } } } | ConvertTo-Json -Depth 10 -Compress))
            [Console]::WriteLine((@{ method = 'thread/tokenUsage/updated'; params = @{ threadId = 'preparation-test-thread'; tokenUsage = @{ last = @{ inputTokens = 40; outputTokens = 12; cachedInputTokens = 0; reasoningOutputTokens = 0 } } } } | ConvertTo-Json -Depth 10 -Compress))
            $reply = @{ method = 'turn/completed'; params = @{ threadId = 'preparation-test-thread'; turn = @{ status = 'completed'; items = @(@{ type = 'agentMessage'; text = 'Preparation fixture answer.' }) } } }
        }
        default { throw 'Unexpected fixture command' }
    }
    [Console]::WriteLine(($reply | ConvertTo-Json -Depth 10 -Compress))
    [Console]::Out.Flush()
}
